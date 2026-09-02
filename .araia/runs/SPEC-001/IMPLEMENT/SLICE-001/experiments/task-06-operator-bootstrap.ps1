[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]{12}$')]
    [string] $ExpectedAccountId,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]{8}t[0-9]{6}z-[a-f0-9]{8}$')]
    [string] $RunId,
    [string] $RunbookPath = (Join-Path $PSScriptRoot 'task-06-aws-matrix.ps1')
)

# Recria a role temporária de operação que o Cleanup anterior removeu, com o
# envelope exato que o Preflight do runbook compara canonicamente: trust policy
# restrita à role administrativa federada, path '/', 3.600 segundos, tags de
# ownership e somente a inline policy ExperimentOperatorPolicy. O envelope é
# extraído do próprio runbook para impedir divergência entre os dois arquivos.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Profile = 'montebravo-admin'
$Region = 'us-east-1'
$AuthorizedRunId = '20260901t115004z-2954eef6'
if ($RunId -ne $AuthorizedRunId) {
    throw 'O RunId não corresponde ao checkpoint autorizado.'
}
$Prefix = "nh-t6-$RunId"
$OperatorRoleName = "$Prefix-operator"
$bootstrapStamp = [System.DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ').ToLowerInvariant()
$StateRoot = Join-Path $env:LOCALAPPDATA "Araia\Task6\$RunId-bootstrap-$bootstrapStamp"
$StatePath = Join-Path $StateRoot 'state.json'
$StateIntegrityKeyPath = Join-Path $StateRoot 'state-integrity-key.dpapi'
$JournalPath = Join-Path $StateRoot 'resource-journal.jsonl'

function Import-RunbookFunction {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Language.ScriptBlockAst] $Ast,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $definition = $Ast.Find(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $Name
        },
        $true
    )
    if ($null -eq $definition) { throw "A função $Name não foi encontrada no runbook." }
    Set-Item -Path "Function:script:$Name" -Value $definition.Body.GetScriptBlock()
}

function Get-RunbookAssignmentText {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Language.ScriptBlockAst] $Ast,
        [Parameter(Mandatory = $true)][string] $VariableName
    )

    $assignments = @($Ast.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq $VariableName
        },
        $true
    ))
    if ($assignments.Count -ne 1) {
        throw "O runbook deve conter exatamente uma atribuição de `$$VariableName; observadas: $($assignments.Count)."
    }
    $assignments[0].Right.Extent.Text
}

$tokens = $null
$parseErrors = $null
$runbookAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $RunbookPath),
    [ref]$tokens,
    [ref]$parseErrors
)
if ($parseErrors.Count -ne 0) { throw 'O runbook contém erros de sintaxe.' }

foreach ($functionName in @(
    'Assert-RestrictedDirectoryAcl',
    'Assert-StateStorageAcl',
    'Initialize-StateStorage',
    'ConvertTo-CanonicalObject',
    'ConvertTo-CanonicalJson',
    'ConvertTo-CanonicalUtcTimestamp',
    'Initialize-AwsCliFileLockType',
    'Get-AwsCliInnermostExceptionMessage',
    'Assert-AncestorDirectoryAcl',
    'Close-AwsCliArgumentLease',
    'New-AwsCliArgumentLease',
    'Assert-AwsCliArgumentLeaseCurrent',
    'Get-AwsErrorCode',
    'Invoke-ProfileAws',
    'Invoke-ProfileAwsSingleAttempt',
    'Test-ExpectedTags'
)) {
    Import-RunbookFunction -Ast $runbookAst -Name $functionName
}

$caller = aws sts get-caller-identity --profile $Profile --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'A sessão SSO não é válida.' }
if ($caller.Arn -match ':root$') { throw 'A sessão administrativa não pode ser root.' }
if ($caller.Account -ne $ExpectedAccountId) {
    throw 'A conta AWS ativa não corresponde à conta aprovada.'
}
if ((aws configure get region --profile $Profile) -ne $Region) {
    throw 'A região do perfil federado não é us-east-1.'
}

$accountId = $ExpectedAccountId
$operatorRoleArn = "arn:aws:iam::$accountId`:role/$OperatorRoleName"
$dataRoleNames = @(
    "$Prefix-upload-a",
    "$Prefix-upload-b",
    "$Prefix-validator-a",
    "$Prefix-disposer-a",
    "$Prefix-dispatch-synthetic",
    "$Prefix-current-version-probe"
)
$dataRoleArns = @($dataRoleNames | ForEach-Object { "arn:aws:iam::$accountId`:role/$_" })
$expectedOperatorPolicy = & ([scriptblock]::Create(
    (Get-RunbookAssignmentText -Ast $runbookAst -VariableName 'expectedOperatorPolicy')
))
if ($expectedOperatorPolicy.Statement.Count -lt 8) {
    throw 'A política extraída do runbook não possui as instruções esperadas.'
}

Initialize-StateStorage
$createdRole = $false
$createdPolicy = $false
try {
    $adminRoleName = ($caller.Arn -split '/')[1]
    $adminRoleResult = Invoke-ProfileAws -Arguments @(
        'iam', 'get-role', '--profile', $Profile, '--role-name', $adminRoleName, '--output', 'json'
    )
    if ($adminRoleResult.ExitCode -ne 0) {
        throw "Não foi possível resolver a role administrativa do IAM Identity Center: $($adminRoleResult.Output)"
    }
    $adminRole = $adminRoleResult.Output | ConvertFrom-Json
    $trustPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Sid = 'TrustExactIdentityCenterAdminRole'
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $adminRole.Role.Arn }
                Action = 'sts:AssumeRole'
            }
        )
    }

    $existing = Invoke-ProfileAws -Arguments @(
        'iam', 'get-role', '--profile', $Profile, '--role-name', $OperatorRoleName, '--output', 'json'
    )
    if ($existing.ExitCode -eq 0) {
        throw 'A role temporária já existe; o bootstrap recusa sobrescrever um recurso preexistente.'
    }
    if ((Get-AwsErrorCode -Output $existing.Output) -ne 'NoSuchEntity') {
        throw "Não foi possível comprovar a ausência da role temporária: $($existing.Output)"
    }

    $roleTags = @(
        [ordered]@{ Key = 'RunId'; Value = $RunId },
        [ordered]@{ Key = 'AraiaTask'; Value = 'Task6' },
        [ordered]@{ Key = 'ManagedBy'; Value = 'Araia' }
    ) | ConvertTo-Json -Compress -AsArray
    $createResult = Invoke-ProfileAwsSingleAttempt -Arguments @(
        'iam', 'create-role', '--profile', $Profile, '--role-name', $OperatorRoleName,
        '--path', '/', '--max-session-duration', '3600',
        '--description', 'Role temporária de operação da matriz da Tarefa 6 (descartável).',
        '--assume-role-policy-document', ($trustPolicy | ConvertTo-Json -Depth 6 -Compress),
        '--tags', $roleTags, '--output', 'json'
    )
    if ($createResult.ExitCode -ne 0) {
        throw "A criação da role temporária falhou ($($createResult.ExitCode)): $($createResult.Output)"
    }
    $createdRole = $true

    $policyResult = Invoke-ProfileAwsSingleAttempt -Arguments @(
        'iam', 'put-role-policy', '--profile', $Profile, '--role-name', $OperatorRoleName,
        '--policy-name', 'ExperimentOperatorPolicy',
        '--policy-document', ($expectedOperatorPolicy | ConvertTo-Json -Depth 8 -Compress)
    )
    if ($policyResult.ExitCode -ne 0) {
        throw "A aplicação da inline policy falhou ($($policyResult.ExitCode)): $($policyResult.Output)"
    }
    $createdPolicy = $true

    $roleReadback = Invoke-ProfileAws -Arguments @(
        'iam', 'get-role', '--profile', $Profile, '--role-name', $OperatorRoleName, '--output', 'json'
    )
    if ($roleReadback.ExitCode -ne 0) {
        throw "A leitura posterior da role falhou: $($roleReadback.Output)"
    }
    $role = ($roleReadback.Output | ConvertFrom-Json).Role
    if ((ConvertTo-CanonicalJson -Value $role.AssumeRolePolicyDocument) -ne
        (ConvertTo-CanonicalJson -Value $trustPolicy) -or
        $role.MaxSessionDuration -ne 3600 -or $role.Path -ne '/' -or
        -not (Test-ExpectedTags -Tags $role.Tags -KeyName 'Key' -ValueName 'Value')) {
        throw 'A role criada divergiu do envelope aprovado.'
    }
    $attached = (Invoke-ProfileAws -Arguments @(
        'iam', 'list-attached-role-policies', '--profile', $Profile,
        '--role-name', $OperatorRoleName, '--output', 'json'
    )).Output | ConvertFrom-Json
    if (@($attached.AttachedPolicies).Count -ne 0) {
        throw 'A role temporária não pode possuir managed policies.'
    }
    $inline = (Invoke-ProfileAws -Arguments @(
        'iam', 'list-role-policies', '--profile', $Profile,
        '--role-name', $OperatorRoleName, '--output', 'json'
    )).Output | ConvertFrom-Json
    if (@($inline.PolicyNames).Count -ne 1 -or $inline.PolicyNames[0] -ne 'ExperimentOperatorPolicy') {
        throw 'A role temporária deve possuir somente ExperimentOperatorPolicy.'
    }
    $observedPolicy = (Invoke-ProfileAws -Arguments @(
        'iam', 'get-role-policy', '--profile', $Profile, '--role-name', $OperatorRoleName,
        '--policy-name', 'ExperimentOperatorPolicy', '--query', 'PolicyDocument', '--output', 'json'
    )).Output | ConvertFrom-Json
    if ((ConvertTo-CanonicalJson -Value $observedPolicy) -ne
        (ConvertTo-CanonicalJson -Value $expectedOperatorPolicy)) {
        throw 'A política efetiva diverge da lista de permissões aprovada.'
    }

    $assumed = $null
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $assumeResult = Invoke-ProfileAwsSingleAttempt -Arguments @(
            'sts', 'assume-role', '--profile', $Profile, '--role-arn', $operatorRoleArn,
            '--role-session-name', 'bootstrap-verification', '--duration-seconds', '900',
            '--output', 'json'
        )
        if ($assumeResult.ExitCode -eq 0) {
            $assumed = $assumeResult.Output | ConvertFrom-Json
            break
        }
        Start-Sleep -Seconds 5
    }
    if ($null -eq $assumed) {
        throw 'A propagação não permitiu assumir a role temporária após 12 tentativas.'
    }
    if ($assumed.AssumedRoleUser.Arn -ne
        "arn:aws:sts::$accountId`:assumed-role/$OperatorRoleName/bootstrap-verification") {
        throw 'A identidade assumida não corresponde à role temporária esperada.'
    }

    [pscustomobject]@{
        Status = 'bootstrapped'
        Account = $accountId
        RunId = $RunId
        OperatorRole = $OperatorRoleName
        OperatorRoleArn = $operatorRoleArn
        TrustedPrincipal = $adminRole.Role.Arn
        InlineStatements = $expectedOperatorPolicy.Statement.Count
        AssumedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    } | ConvertTo-Json -Compress
}
catch {
    if ($createdPolicy) {
        Invoke-ProfileAwsSingleAttempt -Arguments @(
            'iam', 'delete-role-policy', '--profile', $Profile, '--role-name', $OperatorRoleName,
            '--policy-name', 'ExperimentOperatorPolicy'
        ) | Out-Null
    }
    if ($createdRole) {
        Invoke-ProfileAwsSingleAttempt -Arguments @(
            'iam', 'delete-role', '--profile', $Profile, '--role-name', $OperatorRoleName
        ) | Out-Null
    }
    throw
}
finally {
    $stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
    $expectedPrefix = [System.IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA "Araia\Task6\$RunId-bootstrap-")
    )
    if ($stateRootFull.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $StateRoot -PathType Container)) {
        $argumentRoot = Join-Path $StateRoot 'aws-cli-json'
        if (Test-Path -LiteralPath $argumentRoot -PathType Container) {
            foreach ($file in @(Get-ChildItem -LiteralPath $argumentRoot -File)) {
                Remove-Item -LiteralPath $file.FullName -Force
            }
            [System.IO.Directory]::Delete($argumentRoot, $false)
        }
        [System.IO.Directory]::Delete($StateRoot, $false)
    }
}

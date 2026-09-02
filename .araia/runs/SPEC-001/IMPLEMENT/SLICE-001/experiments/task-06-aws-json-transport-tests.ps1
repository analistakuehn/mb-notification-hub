[CmdletBinding()]
param(
    [string] $RunbookPath = (Join-Path $PSScriptRoot 'task-06-aws-matrix.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Message
    )

    if (-not $Condition) { throw $Message }
}

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
    $scriptBlock = $definition.Body.GetScriptBlock()
    Set-Item -Path "Function:script:$Name" -Value $scriptBlock
}

function Set-RestrictedDirectoryAclForTest {
    param([Parameter(Mandatory = $true)][string] $Path)

    $principal = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls $Path '/inheritance:r' "/grant:r" "${principal}:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Não foi possível restringir a ACL do diretório de teste.'
    }
}

if (-not ('AraiaTask6ReparsePointProbeV3' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class AraiaTask6ReparsePointProbeResultV3
{
    public string Stage { get; set; }
    public int Win32Error { get; set; }
    public bool DeviceIoControlCalled { get; set; }
    public bool Succeeded { get; set; }
}

public static class AraiaTask6ReparsePointProbeV3
{
    public const uint GenericWriteAccess = 0x40000000;
    public const uint FileWriteAttributesAccess = 0x00000100;
    public const uint DeleteAccess = 0x00010000;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    public static int TryOpenDirectory(string directoryPath, uint desiredAccess)
    {
        using (SafeFileHandle handle = CreateFile(
            directoryPath,
            desiredAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                return Marshal.GetLastWin32Error();
            }
            return 0;
        }
    }

    public static AraiaTask6ReparsePointProbeResultV3 TrySetMountPoint(
        string directoryPath,
        string targetPath)
    {
        return TrySetMountPoint(directoryPath, targetPath, GenericWriteAccess);
    }

    public static AraiaTask6ReparsePointProbeResultV3 TrySetMountPoint(
        string directoryPath,
        string targetPath,
        uint desiredAccess)
    {
        using (SafeFileHandle handle = CreateFile(
            directoryPath,
            desiredAccess,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                return new AraiaTask6ReparsePointProbeResultV3
                {
                    Stage = "Open",
                    Win32Error = Marshal.GetLastWin32Error(),
                    DeviceIoControlCalled = false,
                    Succeeded = false
                };
            }

            byte[] buffer = BuildMountPointBuffer(targetPath);
            uint bytesReturned;
            bool succeeded = DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                (uint)buffer.Length,
                IntPtr.Zero,
                0,
                out bytesReturned,
                IntPtr.Zero);
            return new AraiaTask6ReparsePointProbeResultV3
            {
                Stage = succeeded ? "Complete" : "DeviceIoControl",
                Win32Error = succeeded ? 0 : Marshal.GetLastWin32Error(),
                DeviceIoControlCalled = true,
                Succeeded = succeeded
            };
        }
    }

    private static byte[] BuildMountPointBuffer(string targetPath)
    {
        string target = Path.GetFullPath(targetPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        byte[] substituteName = Encoding.Unicode.GetBytes("\\??\\" + target + "\0");
        byte[] printName = Encoding.Unicode.GetBytes(target + "\0");
        ushort substituteNameLength = checked((ushort)(substituteName.Length - 2));
        ushort printNameOffset = checked((ushort)substituteName.Length);
        ushort printNameLength = checked((ushort)(printName.Length - 2));
        ushort reparseDataLength = checked((ushort)(
            8 + substituteName.Length + printName.Length));
        byte[] buffer = new byte[8 + reparseDataLength];

        WriteUInt32(buffer, 0, IoReparseTagMountPoint);
        WriteUInt16(buffer, 4, reparseDataLength);
        WriteUInt16(buffer, 8, 0);
        WriteUInt16(buffer, 10, substituteNameLength);
        WriteUInt16(buffer, 12, printNameOffset);
        WriteUInt16(buffer, 14, printNameLength);
        Buffer.BlockCopy(substituteName, 0, buffer, 16, substituteName.Length);
        Buffer.BlockCopy(
            printName,
            0,
            buffer,
            16 + substituteName.Length,
            printName.Length);
        return buffer;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }
}
'@
}

function Convert-WithClosedLease {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $lease = New-AwsCliArgumentLease -Arguments $Arguments
    try {
        $effectiveArguments = @($lease.Arguments)
        foreach ($argument in @($effectiveArguments | Where-Object {
            [string]$_ -match '^file://'
        })) {
            $path = ([string]$argument).Substring('file://'.Length).Replace('/', '\')
            $pathFull = [System.IO.Path]::GetFullPath($path)
            $argumentRootFull = [System.IO.Path]::GetFullPath(
                $script:argumentRoot
            ).TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar
            ) + [System.IO.Path]::DirectorySeparatorChar
            if ($pathFull.StartsWith(
                $argumentRootFull,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                $null = $script:knownArgumentFiles.Add($pathFull)
            }
        }
        $effectiveArguments
    }
    finally { Close-AwsCliArgumentLease -Lease $lease }
}

$tokens = $null
$parseErrors = $null
$runbookAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $RunbookPath),
    [ref]$tokens,
    [ref]$parseErrors
)
Assert-Condition -Condition ($parseErrors.Count -eq 0) `
    -Message 'O runbook contém erros de sintaxe.'

$stateRootAssignment = $runbookAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq 'StateRoot'
    },
    $true
)
Assert-Condition -Condition ($null -ne $stateRootAssignment) `
    -Message 'O runbook não compõe a raiz de estado.'
$stateRootCommand = $stateRootAssignment.Right.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst]
    },
    $true
)
Assert-Condition -Condition ($null -ne $stateRootCommand -and
    $stateRootCommand.GetCommandName() -eq 'Join-Path') `
    -Message 'A raiz de estado não é composta por Join-Path.'
$stateRootElements = @($stateRootCommand.CommandElements)
Assert-Condition -Condition ($stateRootElements.Count -eq 3 -and
    $stateRootElements[1].Extent.Text -eq '$env:LOCALAPPDATA' -and
    $stateRootElements[2].Value -eq 'Araia\Task6\$RunId') `
    -Message "A cauda produtiva da raiz de estado divergiu: $($stateRootCommand.Extent.Text)"

foreach ($functionName in @(
    'Assert-RestrictedDirectoryAcl',
    'Assert-AncestorDirectoryAcl',
    'Assert-StateStorageAcl',
    'Initialize-AwsCliFileLockType',
    'Get-AwsCliInnermostExceptionMessage',
    'Close-AwsCliArgumentLease',
    'Assert-AwsCliArgumentLeaseCurrent',
    'New-AwsCliArgumentLease',
    'ConvertTo-CanonicalUtcTimestamp',
    'Get-AwsErrorCode',
    'Invoke-ProfileAws',
    'Invoke-ProfileAwsSingleAttempt',
    'Invoke-Aws',
    'Get-FailureDisposition',
    'Get-Mutation',
    'Start-MutationIntent',
    'Start-MutationAttempt',
    'Complete-MutationAttempt',
    'Test-AllMutationAttemptsDefinitelyFailed',
    'Complete-Mutation',
    'Invoke-TrackedProfileMutation'
)) {
    Import-RunbookFunction -Ast $runbookAst -Name $functionName
}

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testDirectoryName = "araia-task06-json-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$testEnvelope = Join-Path $temporaryBase $testDirectoryName
$leaseAncestor = Join-Path $testEnvelope 'lease-ancestor'
$fixtureRunId = '20260901t000000z-0000fac7'
$fixtureVendorRoot = Join-Path $leaseAncestor 'Araia'
$fixtureTaskRoot = Join-Path $fixtureVendorRoot 'Task6'
$StateRoot = Join-Path $fixtureTaskRoot $fixtureRunId
$primaryStateRoot = $StateRoot
$argumentRoot = Join-Path $StateRoot 'aws-cli-json'
$argumentJunctionTarget = Join-Path $temporaryBase `
    "araia-task06-json-argument-target-$([guid]::NewGuid().ToString('N'))"
$junctionScenarioRoot = Join-Path $temporaryBase `
    "araia-task06-json-junction-$([guid]::NewGuid().ToString('N'))"
$junctionDestinationRoot = Join-Path $temporaryBase `
    "araia-task06-json-target-$([guid]::NewGuid().ToString('N'))"
$junctionComponent = Join-Path $junctionScenarioRoot 'redirect'
$junctionDestinationStateRoot = Join-Path $junctionDestinationRoot 'state'
$junctionStateRoot = Join-Path $junctionComponent 'state'
$junctionDestinationArgumentRoot = Join-Path $junctionDestinationStateRoot 'aws-cli-json'
$fsctlCalibrationPath = Join-Path $temporaryBase `
    "araia-task06-json-fsctl-$([guid]::NewGuid().ToString('N'))"
$fsctlAttributeCalibrationPath = Join-Path $temporaryBase `
    "araia-task06-json-fsctl-attr-$([guid]::NewGuid().ToString('N'))"
$fsctlCalibrationTarget = Join-Path $temporaryBase `
    "araia-task06-json-fsctl-target-$([guid]::NewGuid().ToString('N'))"
$fsctlLeaseTarget = Join-Path $temporaryBase `
    "araia-task06-json-fsctl-lease-target-$([guid]::NewGuid().ToString('N'))"
$Region = 'us-east-1'
$ExpectedAccountId = 'fixture-account'
$capturedAwsArguments = @()
$stubInvocations = 0
$replacementAttempts = 0
$replacementRejected = $true
$enableFsctlLeaseProbe = $false
$fsctlLeaseResults = @()
$fsctlAttributeLeaseResults = @()
$knownArgumentFiles = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)

try {
    New-Item -ItemType Directory -Path $testEnvelope -Force | Out-Null
    Set-RestrictedDirectoryAclForTest -Path $testEnvelope
    New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
    Set-RestrictedDirectoryAclForTest -Path $StateRoot
    Assert-StateStorageAcl

    New-Item -ItemType Directory -Path $fsctlCalibrationPath | Out-Null
    New-Item -ItemType Directory -Path $fsctlCalibrationTarget | Out-Null
    $fsctlCalibration = [AraiaTask6ReparsePointProbeV3]::TrySetMountPoint(
        $fsctlCalibrationPath,
        $fsctlCalibrationTarget
    )
    Assert-Condition -Condition ($fsctlCalibration.Succeeded -and
        $fsctlCalibration.Stage -eq 'Complete' -and
        $fsctlCalibration.Win32Error -eq 0 -and
        $fsctlCalibration.DeviceIoControlCalled) `
        -Message "A calibração do FSCTL_SET_REPARSE_POINT não teve sucesso real: estágio=$($fsctlCalibration.Stage), erro=$($fsctlCalibration.Win32Error), chamada=$($fsctlCalibration.DeviceIoControlCalled)."
    New-Item -ItemType Directory -Path $fsctlAttributeCalibrationPath | Out-Null
    $fsctlAttributeCalibration = [AraiaTask6ReparsePointProbeV3]::TrySetMountPoint(
        $fsctlAttributeCalibrationPath,
        $fsctlCalibrationTarget,
        [AraiaTask6ReparsePointProbeV3]::FileWriteAttributesAccess
    )
    Assert-Condition -Condition ($fsctlAttributeCalibration.Succeeded -and
        $fsctlAttributeCalibration.Stage -eq 'Complete' -and
        $fsctlAttributeCalibration.Win32Error -eq 0 -and
        $fsctlAttributeCalibration.DeviceIoControlCalled) `
        -Message "A calibração da nova análise por atributo não teve sucesso real: estágio=$($fsctlAttributeCalibration.Stage), erro=$($fsctlAttributeCalibration.Win32Error), chamada=$($fsctlAttributeCalibration.DeviceIoControlCalled)."
    Remove-Item -LiteralPath $fsctlAttributeCalibrationPath -Force
    Remove-Item -LiteralPath $fsctlCalibrationPath -Force
    [System.IO.Directory]::Delete($fsctlCalibrationTarget, $false)

    New-Item -ItemType Directory -Path $argumentJunctionTarget | Out-Null
    New-Item -ItemType Junction -Path $argumentRoot `
        -Target $argumentJunctionTarget | Out-Null
    $junctionRejected = $false
    try {
        $junctionLease = New-AwsCliArgumentLease -Arguments @(
            's3api', 'put-bucket-policy', '--policy', '{"Version":"2012-10-17"}'
        )
        Close-AwsCliArgumentLease -Lease $junctionLease
    }
    catch { $junctionRejected = $true }
    Assert-Condition -Condition $junctionRejected `
        -Message 'Um ponto de nova análise foi aceito no diretório de argumentos.'
    Remove-Item -LiteralPath $argumentRoot -Force
    [System.IO.Directory]::Delete($argumentJunctionTarget, $false)

    New-Item -ItemType Directory -Path $fsctlLeaseTarget | Out-Null

    $tailLease = New-AwsCliArgumentLease -Arguments @(
        's3api', 'put-bucket-policy', '--policy', '{"Version":"2012-10-17"}'
    )
    try {
        foreach ($tailFileEntry in @($tailLease.FileEntries)) {
            $null = $knownArgumentFiles.Add(
                [System.IO.Path]::GetFullPath($tailFileEntry.Path)
            )
        }
        $tailNames = @(@($tailLease.DirectoryEntries) | ForEach-Object {
            [System.IO.Path]::GetFileName($_.Path)
        })
        $observedTail = @($tailNames | Select-Object -Last 4)
        $expectedTail = @('Araia', 'Task6', $fixtureRunId, 'aws-cli-json')
        Assert-Condition -Condition ($observedTail.Count -eq $expectedTail.Count) `
            -Message "A cadeia derivada não tem a profundidade da cauda esperada: $($tailNames -join ', ')."
        for ($index = 0; $index -lt $expectedTail.Count; $index++) {
            Assert-Condition -Condition ([string]::Equals(
                [string]$observedTail[$index],
                [string]$expectedTail[$index],
                [StringComparison]::Ordinal
            )) -Message "O componente $index da cauda divergiu: esperado $($expectedTail[$index]), observado $($observedTail[$index])."
        }
    }
    finally { Close-AwsCliArgumentLease -Lease $tailLease }

    $rawJson = '{ "nome": "ação", "habilitado": true }'
    $jsonParameters = @(
        '--advanced-event-selectors',
        '--assume-role-policy-document',
        '--policy',
        '--policy-document',
        '--public-access-block-configuration',
        '--retention',
        '--server-side-encryption-configuration',
        '--tagging',
        '--tags'
    )

    $firstUri = $null
    foreach ($parameter in $jsonParameters) {
        $converted = @(Convert-WithClosedLease -Arguments @(
            'service', 'operation', $parameter, $rawJson
        ))
        $uri = [string]$converted[3]
        Assert-Condition -Condition $uri.StartsWith('file://') `
            -Message "O parâmetro $parameter não foi convertido para arquivo."
        $path = $uri.Substring('file://'.Length).Replace('/', '\')
        Assert-Condition -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
            -Message "O arquivo de $parameter não foi criado."
        Assert-Condition -Condition ([System.IO.Path]::GetFullPath($path).StartsWith(
            [System.IO.Path]::GetFullPath($argumentRoot),
            [StringComparison]::OrdinalIgnoreCase
        )) -Message "O arquivo de $parameter escapou do diretório restrito."

        $bytes = [System.IO.File]::ReadAllBytes($path)
        $hasBom = $bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        Assert-Condition -Condition (-not $hasBom) `
            -Message "O arquivo de $parameter contém BOM."
        Assert-Condition -Condition (@($bytes | Where-Object { $_ -gt 0x7F }).Count -eq 0) `
            -Message "O arquivo de $parameter não está restrito a ASCII."
        $null = ([System.IO.File]::ReadAllText($path) | ConvertFrom-Json)

        if ($null -eq $firstUri) { $firstUri = $uri }
        $repeated = @(Convert-WithClosedLease -Arguments @(
            'service', 'operation', $parameter, $rawJson
        ))
        Assert-Condition -Condition ($repeated[3] -eq $uri) `
            -Message "O transporte de $parameter não é determinístico."
    }

    $shorthand = @(Convert-WithClosedLease -Arguments @(
        'kms', 'tag-resource', '--tags', 'TagKey=Name,TagValue=fixture'
    ))
    Assert-Condition -Condition ($shorthand[3] -eq 'TagKey=Name,TagValue=fixture') `
        -Message 'A sintaxe abreviada foi alterada.'

    $existingUri = 'file://C:/fixture.json'
    $unchanged = @(Convert-WithClosedLease -Arguments @(
        'iam', 'create-role', '--assume-role-policy-document', $existingUri
    ))
    Assert-Condition -Condition ($unchanged[3] -eq $existingUri) `
        -Message 'Um URI de arquivo existente foi alterado.'

    $invalidRejected = $false
    try {
        $null = New-AwsCliArgumentLease -Arguments @(
            's3api', 'put-bucket-policy', '--policy', '{invalid'
        )
    }
    catch { $invalidRejected = $true }
    Assert-Condition -Condition $invalidRejected `
        -Message 'Um documento JSON inválido foi aceito.'

    function global:aws {
        $script:capturedAwsArguments = @($args)
        $script:stubInvocations++
        if ($script:enableFsctlLeaseProbe) {
            $script:fsctlLeaseResults += `
                [AraiaTask6ReparsePointProbeV3]::TrySetMountPoint(
                    $script:leaseAncestor,
                    $script:fsctlLeaseTarget
                )
            $script:fsctlAttributeLeaseResults += `
                [AraiaTask6ReparsePointProbeV3]::TrySetMountPoint(
                    $script:leaseAncestor,
                    $script:fsctlLeaseTarget,
                    [AraiaTask6ReparsePointProbeV3]::FileWriteAttributesAccess
                )
        }
        $fileUris = @($args | Where-Object {
            [string]$_ -match '^file://'
        } | Select-Object -First 1)
        $fileUri = if ($fileUris.Count -eq 1) { $fileUris[0] } else { $null }
        if ($fileUri) {
            $path = ([string]$fileUri).Substring('file://'.Length).Replace('/', '\')
            $script:replacementAttempts++
            try {
                [System.IO.File]::WriteAllText(
                    $path,
                    '{}',
                    [System.Text.UTF8Encoding]::new($false)
                )
                $script:replacementRejected = $false
            }
            catch { }
            $script:capturedAwsFileContent = [System.IO.File]::ReadAllText($path)
        }
        $global:LASTEXITCODE = 0
        '{}'
    }

    New-Item -ItemType Directory -Path $junctionDestinationStateRoot -Force | Out-Null
    Set-RestrictedDirectoryAclForTest -Path $junctionDestinationStateRoot
    New-Item -ItemType Directory -Path $junctionScenarioRoot | Out-Null
    New-Item -ItemType Junction -Path $junctionComponent `
        -Target $junctionDestinationRoot | Out-Null
    $junctionStubInvocationsBefore = $stubInvocations
    $replacementAttemptsBeforeJunction = $replacementAttempts
    $StateRoot = $junctionStateRoot
    try {
        $junctionResult = Invoke-ProfileAws -Arguments @(
            's3api', 'put-bucket-policy', '--policy', '{"Version":"2012-10-17"}'
        )
        $junctionObservedStubDelta = `
            $stubInvocations - $junctionStubInvocationsBefore
        $junctionDestinationArgumentCreated = `
            Test-Path -LiteralPath $junctionDestinationArgumentRoot
        $junctionBoundaryRejected = $junctionResult.ExitCode -eq 252 -and
            $junctionObservedStubDelta -eq 0 -and
            $junctionResult.Output -match `
                'LocalArgumentPreparationFailure: DirectoryChainReparsePoint' -and
            -not $junctionDestinationArgumentCreated
    }
    finally {
        $StateRoot = $primaryStateRoot
        $stubInvocations = $junctionStubInvocationsBefore
        $replacementAttempts = $replacementAttemptsBeforeJunction
        $replacementRejected = $true

        $junctionJsonBytes = [System.Text.Encoding]::UTF8.GetBytes(
            '{"Version":"2012-10-17"}'
        )
        $junctionJsonHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($junctionJsonBytes)
        ).ToLowerInvariant()
        $junctionArgumentPath = Join-Path $junctionDestinationArgumentRoot `
            "policy-$junctionJsonHash.json"
        if (Test-Path -LiteralPath $junctionArgumentPath -PathType Leaf) {
            Remove-Item -LiteralPath $junctionArgumentPath -Force
        }
        if (Test-Path -LiteralPath $junctionDestinationArgumentRoot -PathType Container) {
            [System.IO.Directory]::Delete($junctionDestinationArgumentRoot, $false)
        }
        if (Test-Path -LiteralPath $junctionComponent) {
            Remove-Item -LiteralPath $junctionComponent -Force
        }
        if (Test-Path -LiteralPath $junctionScenarioRoot -PathType Container) {
            [System.IO.Directory]::Delete($junctionScenarioRoot, $false)
        }
        if (Test-Path -LiteralPath $junctionDestinationStateRoot -PathType Container) {
            [System.IO.Directory]::Delete($junctionDestinationStateRoot, $false)
        }
        if (Test-Path -LiteralPath $junctionDestinationRoot -PathType Container) {
            [System.IO.Directory]::Delete($junctionDestinationRoot, $false)
        }
    }

    $enableFsctlLeaseProbe = $true

    $profileResult = Invoke-ProfileAws -Arguments @(
        'iam', 'create-role', '--assume-role-policy-document', $rawJson
    )
    Assert-Condition -Condition ($profileResult.ExitCode -eq 0) `
        -Message "Invoke-ProfileAws não preservou o resultado do processo (código de saída $($profileResult.ExitCode)): $($profileResult.Output)"
    $profileParameterIndex = [Array]::IndexOf(
        $capturedAwsArguments,
        '--assume-role-policy-document'
    )
    Assert-Condition -Condition ($profileParameterIndex -ge 0) `
        -Message 'Invoke-ProfileAws omitiu o parâmetro JSON.'
    Assert-Condition -Condition ([string]$capturedAwsArguments[$profileParameterIndex + 1]).StartsWith(
        'file://'
    ) -Message 'Invoke-ProfileAws enviou o JSON diretamente na linha de comando.'

    $credential = [pscustomobject]@{
        AccessKeyId = 'x'
        SecretAccessKey = 'x'
        SessionToken = 'x'
    }
    $invokeResult = Invoke-Aws -Credential $credential -Arguments @(
        's3api', 'put-bucket-policy', '--bucket', 'fixture', '--policy', $rawJson
    )
    Assert-Condition -Condition ($invokeResult.ExitCode -eq 0) `
        -Message 'Invoke-Aws não preservou o resultado do processo.'
    $policyIndex = [Array]::IndexOf($capturedAwsArguments, '--policy')
    Assert-Condition -Condition ($policyIndex -ge 0) `
        -Message 'Invoke-Aws omitiu a política.'
    Assert-Condition -Condition ([string]$capturedAwsArguments[$policyIndex + 1]).StartsWith(
        'file://'
    ) -Message 'Invoke-Aws enviou o JSON diretamente na linha de comando.'
    $ownerIndex = [Array]::IndexOf($capturedAwsArguments, '--expected-bucket-owner')
    Assert-Condition -Condition ($ownerIndex -ge 0 -and
        $capturedAwsArguments[$ownerIndex + 1] -eq $ExpectedAccountId) `
        -Message 'Invoke-Aws não preservou o proprietário esperado do bucket.'
    Assert-Condition -Condition ($replacementAttempts -eq 2 -and $replacementRejected) `
        -Message 'O arquivo pôde ser substituído enquanto o processo estava ativo.'
    $fsctlLeaseBlocked = $fsctlLeaseResults.Count -eq 2 -and
        @($fsctlLeaseResults | Where-Object {
            $_.Stage -ne 'Open' -or
            $_.Win32Error -ne 32 -or
            $_.DeviceIoControlCalled -or
            $_.Succeeded
        }).Count -eq 0
    $fsctlAttributeBlocked = $fsctlAttributeLeaseResults.Count -eq 2 -and
        @($fsctlAttributeLeaseResults | Where-Object {
            $_.Stage -ne 'DeviceIoControl' -or
            $_.Win32Error -ne 145 -or
            -not $_.DeviceIoControlCalled -or
            $_.Succeeded
        }).Count -eq 0
    $securityOracleFailures = @()
    if (-not $junctionBoundaryRejected) {
        $securityOracleFailures += `
            "junction ancestral: código=$($junctionResult.ExitCode), chamadas=$junctionObservedStubDelta, destino-criado=$junctionDestinationArgumentCreated, saída=$($junctionResult.Output)"
    }
    if (-not $fsctlAttributeBlocked) {
        $fsctlAttributeDiagnostics = @($fsctlAttributeLeaseResults | ForEach-Object {
            "estágio=$($_.Stage), erro=$($_.Win32Error), chamada=$($_.DeviceIoControlCalled), sucesso=$($_.Succeeded)"
        }) -join '; '
        $securityOracleFailures += `
            "nova análise por atributo durante o lease: $fsctlAttributeDiagnostics"
    }
    if (-not $fsctlLeaseBlocked) {
        $fsctlDiagnostics = @($fsctlLeaseResults | ForEach-Object {
            "estágio=$($_.Stage), erro=$($_.Win32Error), chamada=$($_.DeviceIoControlCalled), sucesso=$($_.Succeeded)"
        }) -join '; '
        $securityOracleFailures += "FSCTL durante lease: $fsctlDiagnostics"
    }
    Assert-Condition -Condition ($securityOracleFailures.Count -eq 0) `
        -Message "As garantias da cadeia de diretórios falharam: $($securityOracleFailures -join ' | ')"
    $capturedDocument = $capturedAwsFileContent | ConvertFrom-Json
    Assert-Condition -Condition ($capturedDocument.nome -eq 'ação' -and
        $capturedDocument.habilitado) `
        -Message 'O processo não leu os bytes verificados pelo lease.'

    Remove-Item -LiteralPath 'Function:\global:aws' -ErrorAction Stop
    $awsExecutable = (Get-Command aws -CommandType Application -ErrorAction Stop).Source
    $cliCases = @(
        [pscustomobject]@{
            Name = 's3-public-access'
            Arguments = @(
                's3api', 'put-public-access-block', '--bucket', 'fixture',
                '--public-access-block-configuration',
                '{"BlockPublicAcls":true,"IgnorePublicAcls":true,"BlockPublicPolicy":true,"RestrictPublicBuckets":true}'
            )
        },
        [pscustomobject]@{
            Name = 's3-encryption'
            Arguments = @(
                's3api', 'put-bucket-encryption', '--bucket', 'fixture',
                '--server-side-encryption-configuration',
                '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"},"BucketKeyEnabled":false}]}'
            )
        },
        [pscustomobject]@{
            Name = 's3-tagging'
            Arguments = @(
                's3api', 'put-bucket-tagging', '--bucket', 'fixture',
                '--tagging', '{"TagSet":[{"Key":"Name","Value":"fixture"}]}'
            )
        },
        [pscustomobject]@{
            Name = 's3-policy'
            Arguments = @(
                's3api', 'put-bucket-policy', '--bucket', 'fixture',
                '--policy', '{"Version":"2012-10-17","Statement":[]}'
            )
        },
        [pscustomobject]@{
            Name = 's3-retention'
            Arguments = @(
                's3api', 'put-object-retention', '--bucket', 'fixture', '--key', 'fixture',
                '--retention',
                '{"Mode":"GOVERNANCE","RetainUntilDate":"2030-01-01T00:00:00Z"}'
            )
        },
        [pscustomobject]@{
            Name = 'cloudtrail-selectors'
            Arguments = @(
                'cloudtrail', 'put-event-selectors', '--trail-name', 'fixture',
                '--advanced-event-selectors',
                '[{"Name":"DataEvents","FieldSelectors":[{"Field":"eventCategory","Equals":["Data"]}]}]'
            )
        },
        [pscustomobject]@{
            Name = 'iam-create-role'
            Arguments = @(
                'iam', 'create-role', '--role-name', 'fixture-role',
                '--assume-role-policy-document',
                '{"Version":"2012-10-17","Statement":[]}',
                '--tags', '[{"Key":"Name","Value":"fixture"}]'
            )
        },
        [pscustomobject]@{
            Name = 'iam-inline-policy'
            Arguments = @(
                'iam', 'put-role-policy', '--role-name', 'fixture-role',
                '--policy-name', 'fixture-policy', '--policy-document',
                '{"Version":"2012-10-17","Statement":[]}'
            )
        },
        [pscustomobject]@{
            Name = 'kms-key'
            Arguments = @(
                'kms', 'create-key', '--description', 'fixture',
                '--key-usage', 'ENCRYPT_DECRYPT', '--key-spec', 'SYMMETRIC_DEFAULT',
                '--origin', 'AWS_KMS', '--policy',
                '{"Version":"2012-10-17","Statement":[]}',
                '--tags', '[{"TagKey":"Name","TagValue":"fixture"}]'
            )
        }
    )
    $savedMetadataDisabled = $env:AWS_EC2_METADATA_DISABLED
    $savedAccessKey = $env:AWS_ACCESS_KEY_ID
    $savedSecretKey = $env:AWS_SECRET_ACCESS_KEY
    $savedSessionToken = $env:AWS_SESSION_TOKEN
    $savedProfile = $env:AWS_PROFILE
    $savedDefaultProfile = $env:AWS_DEFAULT_PROFILE
    $savedMaxAttempts = $env:AWS_MAX_ATTEMPTS
    try {
        $env:AWS_EC2_METADATA_DISABLED = 'true'
        $env:AWS_ACCESS_KEY_ID = 'fixture'
        $env:AWS_SECRET_ACCESS_KEY = 'fixture'
        $env:AWS_MAX_ATTEMPTS = '1'
        Remove-Item -LiteralPath 'Env:AWS_SESSION_TOKEN' -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath 'Env:AWS_PROFILE' -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath 'Env:AWS_DEFAULT_PROFILE' -ErrorAction SilentlyContinue

        foreach ($case in $cliCases) {
            $caseLease = New-AwsCliArgumentLease -Arguments $case.Arguments
            try {
                $converted = @($caseLease.Arguments)
                foreach ($argument in @($converted | Where-Object {
                    [string]$_ -match '^file://'
                })) {
                    $path = ([string]$argument).Substring('file://'.Length).Replace('/', '\')
                    $null = $knownArgumentFiles.Add(
                        [System.IO.Path]::GetFullPath($path)
                    )
                }
                Assert-AwsCliArgumentLeaseCurrent -Lease $caseLease
                if ($case.Name -in @('iam-create-role', 'kms-key')) {
                    $cliOutput = @(& $awsExecutable @converted `
                        --region $Region `
                        --endpoint-url 'http://127.0.0.1:9' `
                        --cli-connect-timeout 1 `
                        --cli-read-timeout 1 `
                        --no-cli-pager 2>&1)
                    $cliText = $cliOutput -join ' '
                    Assert-Condition -Condition ($LASTEXITCODE -eq 255 -and
                        $cliText -match '(Could not connect to|Connect timeout on) endpoint URL') `
                        -Message "A validação local do AWS CLI falhou para $($case.Name): $cliText"
                }
                else {
                    $cliOutput = @(& $awsExecutable @converted `
                        --generate-cli-skeleton output `
                        --region $Region `
                        --endpoint-url 'http://127.0.0.1:9' `
                        --cli-connect-timeout 1 `
                        --cli-read-timeout 1 `
                        --no-cli-pager 2>&1)
                    Assert-Condition -Condition ($LASTEXITCODE -eq 0) `
                        -Message "O parser do AWS CLI recusou $($case.Name): $($cliOutput -join ' ')"
                }
            }
            finally {
                Close-AwsCliArgumentLease -Lease $caseLease
            }
        }
    }
    finally {
        $env:AWS_EC2_METADATA_DISABLED = $savedMetadataDisabled
        $env:AWS_ACCESS_KEY_ID = $savedAccessKey
        $env:AWS_SECRET_ACCESS_KEY = $savedSecretKey
        $env:AWS_SESSION_TOKEN = $savedSessionToken
        $env:AWS_PROFILE = $savedProfile
        $env:AWS_DEFAULT_PROFILE = $savedDefaultProfile
        $env:AWS_MAX_ATTEMPTS = $savedMaxAttempts
    }

    $tamperedPath = $firstUri.Substring('file://'.Length).Replace('/', '\')
    [System.IO.File]::WriteAllText($tamperedPath, '{}', [System.Text.UTF8Encoding]::new($false))
    function global:aws {
        $script:stubInvocations++
        $global:LASTEXITCODE = 0
        '{}'
    }
    $invocationsBeforeTamper = $stubInvocations
    $tamperResult = Invoke-ProfileAws -Arguments @(
        'service', 'operation', '--advanced-event-selectors', $rawJson
    )
    $tamperRejected = $tamperResult.ExitCode -eq 252 -and
        $stubInvocations -eq $invocationsBeforeTamper
    Assert-Condition -Condition $tamperRejected `
        -Message 'A adulteração do arquivo não foi rejeitada antes da criação do processo.'

    function Save-State {
        param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $State)
    }
    $adminRoleName = 'fixture-admin'
    $trackedState = [ordered]@{ Mutations = @() }
    $trackedFailureObserved = $false
    $trackedFailureMessage = $null
    try {
        $null = Invoke-TrackedProfileMutation -State $trackedState `
            -OperationId 'fixture-staging-failure' `
            -EventSource 's3.amazonaws.com' -EventName 'PutBucketPolicy' `
            -ResourceTokens @('fixture') -Arguments @(
                'service', 'operation', '--advanced-event-selectors', $rawJson
            )
    }
    catch {
        $trackedFailureObserved = $true
        $trackedFailureMessage = $_.Exception.Message
    }
    Assert-Condition -Condition ($trackedState.Mutations.Count -eq 1) `
        -Message "A mutação de teste não foi registrada: $trackedFailureMessage"
    $trackedMutation = $trackedState.Mutations[0]
    Assert-Condition -Condition ($trackedFailureObserved -and
        $stubInvocations -eq $invocationsBeforeTamper -and
        $trackedMutation.Status -eq 'not-applied' -and
        $trackedMutation.Attempts.Count -eq 1 -and
        $trackedMutation.Attempts[0].LocalOutcome -eq 'failed-definitive' -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$trackedMutation.Attempts[0].CompletedAt
        )) -Message 'A falha de preparação deixou uma tentativa autenticada inconclusiva.'


    function Get-DirectoryOperationOutcome {
        param(
            [Parameter(Mandatory = $true)][string] $Description,
            [Parameter(Mandatory = $true)][scriptblock] $Action
        )

        try {
            & $Action
            return [pscustomobject]@{
                Description = $Description
                Succeeded = $true
                Win32Error = 0
            }
        }
        catch {
            $operationException = $_.Exception
            while ($null -ne $operationException.InnerException) {
                $operationException = $operationException.InnerException
            }
            return [pscustomobject]@{
                Description = $Description
                Succeeded = $false
                Win32Error = ($operationException.HResult -band 0xFFFF)
            }
        }
    }

    function Get-MutatedHighFileId {
        param([Parameter(Mandatory = $true)][string] $FileId)

        $highDigit = if ($FileId.Substring(16, 1) -eq '0') { 'f' } else { '0' }
        $FileId.Substring(0, 16) + $highDigit + $FileId.Substring(17, 15)
    }

    $leaseMutationFailures = @()
    $leaseMutationCount = 0
    function Test-LeaseMutation {
        param(
            [Parameter(Mandatory = $true)][psobject] $Lease,
            [Parameter(Mandatory = $true)][string] $Description,
            [Parameter(Mandatory = $true)][string] $ExpectedCategory,
            [Parameter(Mandatory = $true)][scriptblock] $Mutate,
            [Parameter(Mandatory = $true)][scriptblock] $Restore
        )

        $script:leaseMutationCount++
        & $Mutate
        $observedMessage = $null
        try { Assert-AwsCliArgumentLeaseCurrent -Lease $Lease }
        catch { $observedMessage = [string]$_.Exception.Message }
        & $Restore
        if ($null -eq $observedMessage) {
            $script:leaseMutationFailures += "$($Description): a revalidação não recusou"
            return
        }
        if (-not $observedMessage.StartsWith(
            $ExpectedCategory,
            [StringComparison]::Ordinal
        )) {
            $script:leaseMutationFailures += "$($Description): $observedMessage"
        }
    }

    $chainEnvelope = Join-Path $temporaryBase `
        "araia-task06-json-chain-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
    $chainStateRoot = Join-Path $chainEnvelope "Araia\Task6\$fixtureRunId"
    $chainArgumentRoot = Join-Path $chainStateRoot 'aws-cli-json'
    $chainAncestorPath = Join-Path $chainEnvelope 'Araia'
    $chainRenamedEnvelope = "$chainEnvelope-renomeado"
    $chainArguments = @(
        's3api', 'put-bucket-policy', '--policy',
        '{"Version":"2012-10-17","Statement":[]}'
    )
    $chainOracleFailures = @()
    New-Item -ItemType Directory -Path $chainEnvelope -Force | Out-Null
    Set-RestrictedDirectoryAclForTest -Path $chainEnvelope
    New-Item -ItemType Directory -Path $chainStateRoot -Force | Out-Null
    Set-RestrictedDirectoryAclForTest -Path $chainStateRoot
    $StateRoot = $chainStateRoot
    try {
        $chainInvocationsBefore = $stubInvocations
        & icacls $chainAncestorPath '/grant' '*S-1-1-0:(OI)(CI)(WD)' | Out-Null
        Assert-Condition -Condition ($LASTEXITCODE -eq 0) `
            -Message 'A concessão de escrita ao principal de teste não foi aplicada.'
        $ancestorAclResult = Invoke-ProfileAws -Arguments $chainArguments
        & icacls $chainAncestorPath '/remove:g' '*S-1-1-0' | Out-Null
        Assert-Condition -Condition ($LASTEXITCODE -eq 0) `
            -Message 'A remoção da concessão de teste não foi aplicada.'
        if (-not ($ancestorAclResult.ExitCode -eq 252 -and
            $stubInvocations -eq $chainInvocationsBefore -and
            $ancestorAclResult.Output -match 'DirectoryChainAncestorAcl' -and
            $ancestorAclResult.Output -match 'S-1-1-0')) {
            $chainOracleFailures += `
                "escrita de terceiro em ancestral: código=$($ancestorAclResult.ExitCode), chamadas=$($stubInvocations - $chainInvocationsBefore), saída=$($ancestorAclResult.Output)"
        }
        $ancestorAclControl = Invoke-ProfileAws -Arguments $chainArguments
        if ($ancestorAclControl.ExitCode -ne 0) {
            $chainOracleFailures += `
                "controle sem a concessão de terceiro: código=$($ancestorAclControl.ExitCode), saída=$($ancestorAclControl.Output)"
        }

        $mutationLease = New-AwsCliArgumentLease -Arguments $chainArguments
        try {
            Assert-AwsCliArgumentLeaseCurrent -Lease $mutationLease
            $identityEntries = @($mutationLease.DirectoryEntries) +
                @($mutationLease.FileEntries)
            foreach ($identityEntry in $identityEntries) {
                if ($identityEntry.FileId -notmatch '^[0-9a-f]{32}$') {
                    $chainOracleFailures += `
                        "identidade fora de 16 bytes em $($identityEntry.Path): $($identityEntry.FileId)"
                }
            }
            $directoryEntry = @($mutationLease.DirectoryEntries)[1]
            $fileEntry = @($mutationLease.FileEntries)[0]
            $originalVolumeName = $mutationLease.VolumeName
            $originalVolumePath = $mutationLease.VolumePath
            $originalStateRootPath = $mutationLease.StateRootPath
            $parentStateRootPath = [System.IO.Path]::GetDirectoryName(
                $originalStateRootPath
            )
            $originalDirectoryPath = $directoryEntry.Path
            $originalDirectoryType = $directoryEntry.Type
            $originalDirectoryAttributes = $directoryEntry.Attributes
            $originalDirectoryVolume = $directoryEntry.Volume
            $originalDirectoryFileId = $directoryEntry.FileId
            $mutatedDirectoryFileId = Get-MutatedHighFileId -FileId $originalDirectoryFileId
            $originalFileType = $fileEntry.Type
            $originalFileHash = $fileEntry.ExpectedHash
            $originalFileAttributes = $fileEntry.Attributes
            $originalFileVolume = $fileEntry.Volume
            $originalFileFileId = $fileEntry.FileId
            $mutatedFileFileId = Get-MutatedHighFileId -FileId $originalFileFileId

            Test-LeaseMutation -Lease $mutationLease `
                -Description 'nome do volume da âncora' `
                -ExpectedCategory 'DirectoryChainIdentityMismatch' `
                -Mutate { $mutationLease.VolumeName = 'volume-inexistente' } `
                -Restore { $mutationLease.VolumeName = $originalVolumeName }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'caminho do volume da âncora' `
                -ExpectedCategory 'DirectoryChainIdentityMismatch' `
                -Mutate { $mutationLease.VolumePath = 'Z:\' } `
                -Restore { $mutationLease.VolumePath = $originalVolumePath }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'profundidade da cadeia' `
                -ExpectedCategory 'DirectoryChainCountMismatch' `
                -Mutate { $mutationLease.StateRootPath = $parentStateRootPath } `
                -Restore { $mutationLease.StateRootPath = $originalStateRootPath }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'caminho de um componente' `
                -ExpectedCategory 'DirectoryChainOrderMismatch' `
                -Mutate { $directoryEntry.Path = 'C:\Windows' } `
                -Restore { $directoryEntry.Path = $originalDirectoryPath }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'tipo de um componente' `
                -ExpectedCategory 'DirectoryChainOrderMismatch' `
                -Mutate { $directoryEntry.Type = 'File' } `
                -Restore { $directoryEntry.Type = $originalDirectoryType }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'atributos de um componente' `
                -ExpectedCategory 'DirectoryChainIdentityMismatch' `
                -Mutate { $directoryEntry.Attributes = $originalDirectoryAttributes -bxor 1 } `
                -Restore { $directoryEntry.Attributes = $originalDirectoryAttributes }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'volume de um componente' `
                -ExpectedCategory 'DirectoryChainIdentityMismatch' `
                -Mutate { $directoryEntry.Volume = $originalDirectoryVolume + 1 } `
                -Restore { $directoryEntry.Volume = $originalDirectoryVolume }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'sequência do registro de um componente' `
                -ExpectedCategory 'DirectoryChainIdentityMismatch' `
                -Mutate { $directoryEntry.FileId = $mutatedDirectoryFileId } `
                -Restore { $directoryEntry.FileId = $originalDirectoryFileId }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'tipo do arquivo de argumento' `
                -ExpectedCategory 'ArgumentFileMetadataMismatch' `
                -Mutate { $fileEntry.Type = 'Directory' } `
                -Restore { $fileEntry.Type = $originalFileType }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'ausência do hash esperado' `
                -ExpectedCategory 'ArgumentFileMetadataMismatch' `
                -Mutate { $fileEntry.ExpectedHash = '' } `
                -Restore { $fileEntry.ExpectedHash = $originalFileHash }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'hash esperado do arquivo' `
                -ExpectedCategory 'ArgumentFileHashMismatch' `
                -Mutate { $fileEntry.ExpectedHash = '0' * 64 } `
                -Restore { $fileEntry.ExpectedHash = $originalFileHash }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'atributos do arquivo de argumento' `
                -ExpectedCategory 'ArgumentFileIdentityMismatch' `
                -Mutate { $fileEntry.Attributes = $originalFileAttributes -bxor 1 } `
                -Restore { $fileEntry.Attributes = $originalFileAttributes }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'volume do arquivo de argumento' `
                -ExpectedCategory 'ArgumentFileIdentityMismatch' `
                -Mutate { $fileEntry.Volume = $originalFileVolume + 1 } `
                -Restore { $fileEntry.Volume = $originalFileVolume }
            Test-LeaseMutation -Lease $mutationLease `
                -Description 'sequência do registro do arquivo de argumento' `
                -ExpectedCategory 'ArgumentFileIdentityMismatch' `
                -Mutate { $fileEntry.FileId = $mutatedFileFileId } `
                -Restore { $fileEntry.FileId = $originalFileFileId }

            $mutationLease.RequiresValidation = $false
            $fileEntry.ExpectedHash = '0' * 64
            $validationSkipped = $true
            try { Assert-AwsCliArgumentLeaseCurrent -Lease $mutationLease }
            catch { $validationSkipped = $false }
            $fileEntry.ExpectedHash = $originalFileHash
            $mutationLease.RequiresValidation = $true
            if (-not $validationSkipped) {
                $chainOracleFailures += `
                    'a revalidação recusou com a verificação desligada, o que impede atribuir as demais recusas às comparações'
            }
            Assert-AwsCliArgumentLeaseCurrent -Lease $mutationLease
        }
        finally { Close-AwsCliArgumentLease -Lease $mutationLease }

        $callSiteInvocationsBefore = $stubInvocations
        $script:originalLeaseAssertion = ${function:Assert-AwsCliArgumentLeaseCurrent}
        ${function:Assert-AwsCliArgumentLeaseCurrent} = {
            param([Parameter(Mandatory = $true)][psobject] $Lease)

            @($Lease.FileEntries)[0].ExpectedHash = '0' * 64
            & $script:originalLeaseAssertion -Lease $Lease
        }
        try {
            $callSiteResult = Invoke-ProfileAws -Arguments $chainArguments
        }
        finally {
            ${function:Assert-AwsCliArgumentLeaseCurrent} = $script:originalLeaseAssertion
        }
        if (-not ($callSiteResult.ExitCode -eq 252 -and
            $stubInvocations -eq $callSiteInvocationsBefore -and
            $callSiteResult.Output -match 'ArgumentFileHashMismatch')) {
            $chainOracleFailures += `
                "revalidação anterior ao processo: código=$($callSiteResult.ExitCode), chamadas=$($stubInvocations - $callSiteInvocationsBefore), saída=$($callSiteResult.Output)"
        }
        $callSiteControl = Invoke-ProfileAws -Arguments $chainArguments
        if ($callSiteControl.ExitCode -ne 0) {
            $chainOracleFailures += `
                "controle da revalidação: código=$($callSiteControl.ExitCode), saída=$($callSiteControl.Output)"
        }

        $chainLease = New-AwsCliArgumentLease -Arguments $chainArguments
        $ancestorSwapOutcomes = @()
        $heldOpenRefusals = @()
        try {
            foreach ($heldEntry in @($chainLease.DirectoryEntries)) {
                $heldOpenRefusals += [pscustomobject]@{
                    Path = $heldEntry.Path
                    Win32Error = [AraiaTask6ReparsePointProbeV3]::TryOpenDirectory(
                        $heldEntry.Path,
                        [AraiaTask6ReparsePointProbeV3]::DeleteAccess
                    )
                }
            }
            $ancestorSwapOutcomes += Get-DirectoryOperationOutcome `
                -Description 'renomear o envelope mantido mais raso' `
                -Action {
                    [System.IO.Directory]::Move($chainEnvelope, $chainRenamedEnvelope)
                }
            $ancestorSwapOutcomes += Get-DirectoryOperationOutcome `
                -Description 'renomear o componente mantido mais profundo' `
                -Action {
                    [System.IO.Directory]::Move(
                        $chainArgumentRoot,
                        "$chainArgumentRoot-renomeado"
                    )
                }
            $ancestorSwapOutcomes += Get-DirectoryOperationOutcome `
                -Description 'excluir o componente mantido mais profundo' `
                -Action { [System.IO.Directory]::Delete($chainArgumentRoot, $true) }
        }
        finally { Close-AwsCliArgumentLease -Lease $chainLease }
        foreach ($swapOutcome in $ancestorSwapOutcomes) {
            if ($swapOutcome.Succeeded -or $swapOutcome.Win32Error -ne 32) {
                $chainOracleFailures += `
                    "$($swapOutcome.Description): sucesso=$($swapOutcome.Succeeded), erro=$($swapOutcome.Win32Error)"
            }
        }
        $heldChainDepth = $heldOpenRefusals.Count
        $heldDeleteOpenErrors = @($heldOpenRefusals | ForEach-Object {
            $heldName = [System.IO.Path]::GetFileName($_.Path)
            if ([string]::IsNullOrEmpty($heldName)) { $heldName = $_.Path }
            "$($heldName)=$($_.Win32Error)"
        }) -join ','
        foreach ($heldOpenRefusal in $heldOpenRefusals) {
            if ($heldOpenRefusal.Win32Error -eq 0) {
                $chainOracleFailures += `
                    "abertura para exclusão permitida em $($heldOpenRefusal.Path)"
            }
        }
        $envelopeRefusal = @($heldOpenRefusals | Where-Object {
            $_.Path -eq $chainEnvelope
        })
        if ($envelopeRefusal.Count -ne 1 -or $envelopeRefusal[0].Win32Error -ne 32) {
            $chainOracleFailures += `
                "o envelope mantido mais raso não recusou a abertura para exclusão com 32: $heldDeleteOpenErrors"
        }
        $swapControl = Get-DirectoryOperationOutcome `
            -Description 'renomear o envelope com o lease encerrado' `
            -Action {
                [System.IO.Directory]::Move($chainEnvelope, $chainRenamedEnvelope)
            }
        if (-not $swapControl.Succeeded) {
            $chainOracleFailures += `
                "controle da troca de ancestral: erro=$($swapControl.Win32Error)"
        }
        else {
            [System.IO.Directory]::Move($chainRenamedEnvelope, $chainEnvelope)
        }
        Assert-Condition -Condition ($chainOracleFailures.Count -eq 0 -and
            $leaseMutationFailures.Count -eq 0) `
            -Message "Os oráculos da cadeia falharam: $((@($chainOracleFailures) + @($leaseMutationFailures)) -join ' | ')"
    }
    finally {
        $StateRoot = $primaryStateRoot
        $chainPrefix = Join-Path $temporaryBase 'araia-task06-json-chain-'
        foreach ($chainPath in @($chainRenamedEnvelope, $chainEnvelope)) {
            if (-not (Test-Path -LiteralPath $chainPath -PathType Container)) { continue }
            if (-not ([System.IO.Path]::GetFullPath($chainPath)).StartsWith(
                $chainPrefix,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw 'O envelope da cadeia escapou do prefixo autorizado.'
            }
            Remove-Item -LiteralPath $chainPath -Recurse -Force
        }
    }

    $ancestorSwapErrors = @($ancestorSwapOutcomes | ForEach-Object {
        "$($_.Win32Error)"
    }) -join ','

    [pscustomobject]@{
        Status = 'PASS'
        JsonParameters = $jsonParameters.Count
        WrapperInvocations = $stubInvocations
        CliParserCommands = $cliCases.Count
        SkeletonCommands = 7
        LoopbackValidationCommands = 2
        AwsServiceRequests = 0
        Utf8WithoutBom = $true
        Deterministic = $true
        TamperRejected = $true
        JunctionRejected = $junctionRejected
        AncestorJunctionRejected = $junctionBoundaryRejected
        DirectoryChainWriteBlocked = $fsctlLeaseBlocked
        ReplacementRejected = $replacementRejected
        StagingConverged = $true
        ProductionTailAsserted = $true
        AncestorReparseRefusedByEmptiness = $fsctlAttributeBlocked
        LeaseMutations = $leaseMutationCount
        HeldChainDepth = $heldChainDepth
        HeldDeleteOpenErrors = $heldDeleteOpenErrors
        AncestorSwapErrors = $ancestorSwapErrors
    } | ConvertTo-Json -Compress
}
finally {
    Remove-Item -LiteralPath 'Function:\global:aws' -ErrorAction SilentlyContinue

    $StateRoot = $primaryStateRoot
    $stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
    $expectedPrefix = Join-Path $temporaryBase 'araia-task06-json-'
    if (-not $stateRootFull.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'O diretório temporário escapou do prefixo autorizado.'
    }
    $argumentRootFull = [System.IO.Path]::GetFullPath($argumentRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($knownArgumentFile in @($knownArgumentFiles)) {
        $knownArgumentFileFull = [System.IO.Path]::GetFullPath($knownArgumentFile)
        $junctionScenarioRootFull = [System.IO.Path]::GetFullPath(
            $junctionScenarioRoot
        ).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        ) + [System.IO.Path]::DirectorySeparatorChar
        if ($knownArgumentFileFull.StartsWith(
            $junctionScenarioRootFull,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            continue
        }
        if (-not $knownArgumentFileFull.StartsWith(
            $argumentRootFull,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw "Um arquivo temporário conhecido escapou do diretório autorizado: arquivo=$knownArgumentFileFull; principal=$argumentRootFull; junction=$junctionScenarioRootFull."
        }
        if (Test-Path -LiteralPath $knownArgumentFileFull -PathType Leaf) {
            Remove-Item -LiteralPath $knownArgumentFileFull -Force
        }
    }
    if (Test-Path -LiteralPath $argumentRoot -PathType Container) {
        [System.IO.Directory]::Delete($argumentRoot, $false)
    }
    if (Test-Path -LiteralPath $StateRoot -PathType Container) {
        [System.IO.Directory]::Delete($StateRoot, $false)
    }
    foreach ($fixtureTailPath in @($fixtureTaskRoot, $fixtureVendorRoot)) {
        if (Test-Path -LiteralPath $fixtureTailPath -PathType Container) {
            [System.IO.Directory]::Delete($fixtureTailPath, $false)
        }
    }
    if (Test-Path -LiteralPath $leaseAncestor -PathType Container) {
        [System.IO.Directory]::Delete($leaseAncestor, $false)
    }
    if (Test-Path -LiteralPath $testEnvelope -PathType Container) {
        [System.IO.Directory]::Delete($testEnvelope, $false)
    }

    foreach ($junctionPath in @(
        $fsctlCalibrationPath,
        $fsctlAttributeCalibrationPath,
        $junctionComponent
    )) {
        if (Test-Path -LiteralPath $junctionPath) {
            Remove-Item -LiteralPath $junctionPath -Force
        }
    }
    foreach ($directoryPath in @(
        $junctionDestinationArgumentRoot,
        $junctionDestinationStateRoot,
        $junctionScenarioRoot,
        $junctionDestinationRoot,
        $argumentJunctionTarget,
        $fsctlCalibrationTarget,
        $fsctlLeaseTarget
    )) {
        if (Test-Path -LiteralPath $directoryPath -PathType Container) {
            [System.IO.Directory]::Delete($directoryPath, $false)
        }
    }
}

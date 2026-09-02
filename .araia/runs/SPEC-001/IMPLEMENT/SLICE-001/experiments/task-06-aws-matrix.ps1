param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'Provision', 'Execute', 'CollectEvidence', 'Cleanup', 'VerifyCleanup')]
    [string] $Phase,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]{12}$')]
    [string] $ExpectedAccountId,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]{8}t[0-9]{6}z-[a-f0-9]{8}$')]
    [string] $RunId,
    [ValidateSet(
        'None', 'final-delete-trail', 'final-delete-trail-bucket',
        'final-delete-operator-policy', 'final-delete-operator-role'
    )]
    [string] $FinalizationFaultAfter = 'None'
)

$ErrorActionPreference = 'Stop'
$Profile = 'montebravo-admin'
$Region = 'us-east-1'
$AuthorizedRunId = '20260901t115004z-2954eef6'
if ($RunId -ne $AuthorizedRunId) {
    throw 'O RunId não corresponde ao checkpoint autorizado.'
}
$Prefix = "nh-t6-$RunId"
$OperatorRoleName = "$Prefix-operator"
$StateRoot = Join-Path $env:LOCALAPPDATA "Araia\Task6\$RunId"
$StatePath = Join-Path $StateRoot 'state.json'
$StateIntegrityKeyPath = Join-Path $StateRoot 'state-integrity-key.dpapi'
$JournalPath = Join-Path $StateRoot 'resource-journal.jsonl'
$CommonLogPath = Join-Path $StateRoot 'common.log'
$RestrictedEvidencePath = Join-Path $StateRoot 'restricted-evidence.jsonl'

if (-not ('AraiaTask6CryptographicOperations' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;

public static class AraiaTask6CryptographicOperations
{
    public static void ZeroMemory(byte[] buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        CryptographicOperations.ZeroMemory(buffer);
    }

    public static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left == null) throw new ArgumentNullException(nameof(left));
        if (right == null) throw new ArgumentNullException(nameof(right));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
'@
}

function Invoke-FinalizationFault {
    param([Parameter(Mandatory = $true)][string] $OperationId)

    if ($FinalizationFaultAfter -eq $OperationId) {
        throw "Falha de retomada injetada após $OperationId e antes da confirmação durável."
    }
}

function Assert-RestrictedDirectoryAcl {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw 'O diretório restrito não existe.'
    }
    $directory = Get-Item -LiteralPath $Path -Force
    if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'O diretório restrito não pode ser um ponto de nova análise.'
    }

    $currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $currentSid = $currentIdentity.User.Value
    $acl = Get-Acl -LiteralPath $Path
    $ownerSid = ([System.Security.Principal.NTAccount]::new($acl.Owner)).Translate(
        [System.Security.Principal.SecurityIdentifier]
    ).Value
    if ($ownerSid -ne $currentSid -or -not $acl.AreAccessRulesProtected) {
        throw 'O proprietário ou a proteção contra herança da ACL de estado divergiu.'
    }

    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [System.Security.Principal.SecurityIdentifier]
    ))
    if ($rules.Count -ne 1) {
        throw 'A ACL de estado contém principals adicionais.'
    }
    $rule = $rules[0]
    if ($rule.IdentityReference.Value -ne $currentSid -or
        $rule.IsInherited -or
        $rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
        ($rule.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -ne
            [System.Security.AccessControl.FileSystemRights]::FullControl) {
        throw 'A ACL de estado não concede FullControl exclusivamente ao usuário atual.'
    }
}

function Assert-AncestorDirectoryAcl {
    param([Parameter(Mandatory = $true)][string] $Path)

    $writeRights = [System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership
    $toleratedSids = @(
        [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',
        'S-1-5-32-544'
    )
    $acl = Get-Acl -LiteralPath $Path
    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [System.Security.Principal.SecurityIdentifier]
    ))
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne
            [System.Security.AccessControl.AccessControlType]::Allow) {
            continue
        }
        $ruleSid = $rule.IdentityReference.Value
        if ($toleratedSids -contains $ruleSid) { continue }
        if (($rule.FileSystemRights -band $writeRights) -eq 0) { continue }
        throw "DirectoryChainAncestorAcl: O componente $Path concede escrita ao principal $ruleSid."
    }
}

function Assert-StateStorageAcl {
    Assert-RestrictedDirectoryAcl -Path $StateRoot
}

function Initialize-StateStorage {
    if (-not (Test-Path -LiteralPath $StateRoot)) {
        New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
        $principal = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        & icacls $StateRoot '/inheritance:r' "/grant:r" "${principal}:(OI)(CI)F" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Não foi possível restringir a ACL do diretório de evidência.'
        }
    }
    Assert-StateStorageAcl
}

function Get-StateIntegrityKey {
    param([switch] $Create)

    $entropy = [System.Text.Encoding]::UTF8.GetBytes("Araia.Task6.$RunId")
    if (-not (Test-Path -LiteralPath $StateIntegrityKeyPath)) {
        if (-not $Create) {
            throw 'A chave protegida de integridade do estado não existe.'
        }
        $key = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        try {
            $protectedKey = [System.Security.Cryptography.ProtectedData]::Protect(
                $key,
                $entropy,
                [System.Security.Cryptography.DataProtectionScope]::CurrentUser
            )
            $temporaryKeyPath = "$StateIntegrityKeyPath.tmp"
            [Convert]::ToBase64String($protectedKey) |
                Set-Content -LiteralPath $temporaryKeyPath -Encoding utf8
            Move-Item -LiteralPath $temporaryKeyPath -Destination $StateIntegrityKeyPath -Force
            return ,$key
        }
        catch {
            [AraiaTask6CryptographicOperations]::ZeroMemory($key)
            throw
        }
    }

    $protectedKey = [Convert]::FromBase64String(
        (Get-Content -Raw -LiteralPath $StateIntegrityKeyPath).Trim()
    )
    $key = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $protectedKey,
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    return ,$key
}

function ConvertTo-CanonicalObject {
    param([AllowNull()][object] $Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [ValueType]) { return $Value }

    if ($Value -is [System.Collections.IDictionary]) {
        $canonical = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object { $_.ToString() })) {
            $canonical[$key] = ConvertTo-CanonicalObject -Value $Value[$key]
        }
        return $canonical
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $canonical = [ordered]@{}
        foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
            $canonical[$property.Name] = ConvertTo-CanonicalObject -Value $property.Value
        }
        return $canonical
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $items = @($Value | ForEach-Object { ConvertTo-CanonicalObject -Value $_ })
        return @($items | Sort-Object { $_ | ConvertTo-Json -Depth 30 -Compress })
    }

    $Value
}

function ConvertTo-CanonicalJson {
    param([Parameter(Mandatory = $true)][object] $Value)

    ConvertTo-CanonicalObject -Value $Value | ConvertTo-Json -Depth 30 -Compress
}

function ConvertFrom-IsoTimestamp {
    param([Parameter(Mandatory = $true)][string] $Value)

    [System.DateTimeOffset]::Parse(
        $Value,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind
    )
}

function ConvertTo-CanonicalUtcTimestamp {
    param([Parameter(Mandatory = $true)][System.DateTimeOffset] $Value)

    $Value.UtcDateTime.ToString(
        'O',
        [System.Globalization.CultureInfo]::InvariantCulture
    )
}

function Initialize-AwsCliFileLockType {
    if ('AraiaTask6FileLocksV2' -as [type]) { return }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class AraiaTask6CanonicalPathV2
{
    public string FullPath { get; set; }
    public string VolumePath { get; set; }
    public string VolumeName { get; set; }
    public string[] DirectoryPaths { get; set; }
}

public sealed class AraiaTask6LockedPathV2 : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileStream stream;

    internal AraiaTask6LockedPathV2(
        string path,
        string objectType,
        uint fileAttributes,
        ulong volumeSerialNumber,
        string fileId,
        SafeFileHandle handle,
        FileStream stream)
    {
        Path = path;
        ObjectType = objectType;
        FileAttributes = fileAttributes;
        VolumeSerialNumber = volumeSerialNumber;
        FileId = fileId;
        this.handle = handle;
        this.stream = stream;
    }

    public string Path { get; private set; }
    public string ObjectType { get; private set; }
    public uint FileAttributes { get; private set; }
    public ulong VolumeSerialNumber { get; private set; }
    public string FileId { get; private set; }
    public SafeFileHandle Handle { get { return handle; } }
    public FileStream Stream { get { return stream; } }

    public void Dispose()
    {
        if (stream != null)
        {
            stream.Dispose();
            return;
        }
        handle.Dispose();
    }
}

public static class AraiaTask6FileLocksV2
{
    private const uint GenericRead = 0x80000000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong LowPart;
        public ulong HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileBasicInformationByHandle(
        SafeFileHandle handle,
        FileInfoByHandleClass informationClass,
        out FileBasicInfo information,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformationByHandle(
        SafeFileHandle handle,
        FileInfoByHandleClass informationClass,
        out FileIdInfo information,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);

    public static AraiaTask6CanonicalPathV2 CanonicalizeStateRoot(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "DirectoryChainUnsupportedPath: O caminho do estado está vazio.");
        }

        string fullPath = Path.GetFullPath(path);
        string untrimmedRootPath = Path.GetPathRoot(fullPath);
        if (!String.Equals(
            fullPath,
            untrimmedRootPath,
            StringComparison.OrdinalIgnoreCase))
        {
            fullPath = fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
        if (!Path.IsPathFullyQualified(fullPath) ||
            fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            fullPath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "DirectoryChainUnsupportedPath: O caminho do estado não é local e absoluto.");
        }

        string rootPath = Path.GetPathRoot(fullPath);
        if (String.IsNullOrEmpty(rootPath) ||
            rootPath.Length != 3 ||
            !Char.IsLetter(rootPath[0]) ||
            rootPath[1] != Path.VolumeSeparatorChar ||
            (rootPath[2] != Path.DirectorySeparatorChar &&
                rootPath[2] != Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException(
                "DirectoryChainUnsupportedPath: A raiz do estado não é uma unidade local prevista.");
        }

        StringBuilder volumePath = new StringBuilder(1024);
        if (!GetVolumePathName(fullPath, volumePath, volumePath.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        string stableVolumePath = EnsureTrailingSeparator(volumePath.ToString());
        string stableRootPath = EnsureTrailingSeparator(rootPath);
        if (!String.Equals(
            stableVolumePath,
            stableRootPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "DirectoryChainMountPoint: O caminho do estado atravessa um ponto de montagem.");
        }

        StringBuilder volumeName = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(
            stableVolumePath,
            volumeName,
            volumeName.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        string relativePath = fullPath.Substring(stableRootPath.Length);
        string[] components = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        string[] directoryPaths = new string[components.Length + 1];
        directoryPaths[0] = stableRootPath;
        string currentPath = stableRootPath;
        for (int index = 0; index < components.Length; index++)
        {
            currentPath = Path.Combine(currentPath, components[index]);
            directoryPaths[index + 1] = currentPath;
        }

        return new AraiaTask6CanonicalPathV2
        {
            FullPath = fullPath,
            VolumePath = stableVolumePath,
            VolumeName = volumeName.ToString(),
            DirectoryPaths = directoryPaths
        };
    }

    public static AraiaTask6LockedPathV2 OpenDirectory(string path)
    {
        return OpenLocked(
            path,
            FileListDirectory | FileReadAttributes,
            FileShareRead,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            true);
    }

    public static AraiaTask6LockedPathV2 InspectDirectory(string path)
    {
        return OpenLocked(
            path,
            FileReadAttributes,
            FileShareRead,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            true);
    }

    public static AraiaTask6LockedPathV2 OpenFile(string path)
    {
        return OpenLocked(
            path,
            GenericRead,
            FileShareRead,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            false);
    }

    private static AraiaTask6LockedPathV2 OpenLocked(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint flags,
        bool expectDirectory)
    {
        SafeFileHandle handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        FileBasicInfo basicInformation;
        if (!GetFileBasicInformationByHandle(
            handle,
            FileInfoByHandleClass.FileBasicInfo,
            out basicInformation,
            (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if ((basicInformation.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException(
                "DirectoryChainReparsePoint: Um componente do caminho é um ponto de nova análise.");
        }
        bool isDirectory =
            (basicInformation.FileAttributes & FileAttributeDirectory) != 0;
        if (isDirectory != expectDirectory)
        {
            handle.Dispose();
            throw new InvalidDataException(
                "DirectoryChainObjectType: O tipo do objeto aberto não corresponde ao esperado.");
        }

        FileIdInfo idInformation;
        if (!GetFileIdInformationByHandle(
            handle,
            FileInfoByHandleClass.FileIdInfo,
            out idInformation,
            (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        FileStream stream = expectDirectory
            ? null
            : new FileStream(handle, FileAccess.Read, 4096, false);
        string fileId = idInformation.FileId.LowPart.ToString("x16") +
            idInformation.FileId.HighPart.ToString("x16");
        return new AraiaTask6LockedPathV2(
            path,
            expectDirectory ? "Directory" : "File",
            basicInformation.FileAttributes,
            idInformation.VolumeSerialNumber,
            fileId,
            handle,
            stream);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
'@
}

function Get-AwsCliInnermostExceptionMessage {
    param([Parameter(Mandatory = $true)][System.Exception] $Exception)

    $currentException = $Exception
    while ($null -ne $currentException.InnerException) {
        $currentException = $currentException.InnerException
    }
    $currentException.Message
}

function Close-AwsCliArgumentLease {
    param([AllowNull()][psobject] $Lease)

    if ($null -eq $Lease) { return }
    foreach ($fileEntry in @($Lease.FileEntries)) {
        try { $fileEntry.Lock.Dispose() } catch { }
    }
    $directoryEntries = @($Lease.DirectoryEntries)
    for ($index = $directoryEntries.Count - 1; $index -ge 0; $index--) {
        try { $directoryEntries[$index].Lock.Dispose() } catch { }
    }
}

function Assert-AwsCliArgumentLeaseCurrent {
    param([Parameter(Mandatory = $true)][psobject] $Lease)

    if (-not $Lease.RequiresValidation) { return }

    $validationDirectories = [System.Collections.Generic.List[object]]::new()
    $validationFiles = [System.Collections.Generic.List[object]]::new()
    try {
        try {
            $canonicalPath = [AraiaTask6FileLocksV2]::CanonicalizeStateRoot(
                $Lease.StateRootPath
            )
        }
        catch {
            throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
        }
        if ($canonicalPath.FullPath -ne $Lease.StateRootPath -or
            $canonicalPath.VolumePath -ne $Lease.VolumePath -or
            $canonicalPath.VolumeName -ne $Lease.VolumeName) {
            throw 'DirectoryChainIdentityMismatch: A âncora do volume divergiu.'
        }

        $expectedDirectories = @($Lease.DirectoryEntries)
        if ($expectedDirectories.Count -ne $canonicalPath.DirectoryPaths.Count + 1) {
            throw 'DirectoryChainCountMismatch: A quantidade de diretórios divergiu.'
        }
        for ($index = 0; $index -lt $expectedDirectories.Count; $index++) {
            $expected = $expectedDirectories[$index]
            $expectedPath = if ($index -lt $canonicalPath.DirectoryPaths.Count) {
                $canonicalPath.DirectoryPaths[$index]
            }
            else { $Lease.ArgumentRootPath }
            if ($expected.Path -ne $expectedPath -or $expected.Type -ne 'Directory') {
                throw 'DirectoryChainOrderMismatch: A ordem ou o tipo dos diretórios divergiu.'
            }
            try {
                $observed = [AraiaTask6FileLocksV2]::OpenDirectory($expected.Path)
            }
            catch {
                throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
            }
            $validationDirectories.Add($observed)
            if ($observed.ObjectType -ne $expected.Type -or
                $observed.FileAttributes -ne $expected.Attributes -or
                $observed.VolumeSerialNumber -ne $expected.Volume -or
                $observed.FileId -ne $expected.FileId) {
                throw 'DirectoryChainIdentityMismatch: A identidade de um diretório divergiu.'
            }
        }

        $expectedFiles = @($Lease.FileEntries)
        for ($index = 0; $index -lt $expectedFiles.Count; $index++) {
            $expected = $expectedFiles[$index]
            if ($expected.Type -ne 'File' -or
                [string]::IsNullOrWhiteSpace([string]$expected.ExpectedHash)) {
                throw 'ArgumentFileMetadataMismatch: Os metadados do arquivo estão incompletos.'
            }
            try {
                $observed = [AraiaTask6FileLocksV2]::OpenFile($expected.Path)
            }
            catch {
                throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
            }
            $validationFiles.Add($observed)
            if ($observed.ObjectType -ne $expected.Type -or
                $observed.FileAttributes -ne $expected.Attributes -or
                $observed.VolumeSerialNumber -ne $expected.Volume -or
                $observed.FileId -ne $expected.FileId) {
                throw 'ArgumentFileIdentityMismatch: A identidade do arquivo divergiu.'
            }
            $observedHash = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($observed.Stream)
            ).ToLowerInvariant()
            $observed.Stream.Position = 0
            if ($observedHash -ne $expected.ExpectedHash) {
                throw 'ArgumentFileHashMismatch: O conteúdo do arquivo divergiu.'
            }
        }
    }
    finally {
        foreach ($validationFile in @($validationFiles)) {
            try { $validationFile.Dispose() } catch { }
        }
        for ($index = $validationDirectories.Count - 1; $index -ge 0; $index--) {
            try { $validationDirectories[$index].Dispose() } catch { }
        }
    }
}

function New-AwsCliArgumentLease {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $jsonParameters = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal
    )
    foreach ($parameter in @(
        '--advanced-event-selectors', '--assume-role-policy-document',
        '--policy', '--policy-document', '--public-access-block-configuration',
        '--retention', '--server-side-encryption-configuration', '--tagging', '--tags'
    )) {
        $null = $jsonParameters.Add($parameter)
    }

    $effectiveArguments = @($Arguments)
    $documents = @()
    for ($index = 0; $index -lt $effectiveArguments.Count - 1; $index++) {
        $parameter = $effectiveArguments[$index]
        if (-not $jsonParameters.Contains($parameter)) { continue }

        $value = [string]$effectiveArguments[$index + 1]
        if ($value.StartsWith('file://', [StringComparison]::OrdinalIgnoreCase)) {
            $index++
            continue
        }
        $trimmedValue = $value.TrimStart()
        if (-not ($trimmedValue.StartsWith('{', [StringComparison]::Ordinal) -or
            $trimmedValue.StartsWith('[', [StringComparison]::Ordinal))) {
            $index++
            continue
        }

        try {
            $document = $value | ConvertFrom-Json -AsHashtable -DateKind String `
                -NoEnumerate
        }
        catch {
            throw "O argumento $parameter não contém JSON válido."
        }
        $canonicalJson = ConvertTo-Json -InputObject $document -Depth 30 -Compress `
            -EscapeHandling EscapeNonAscii
        $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($canonicalJson)
        $contentHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($jsonBytes)
        ).ToLowerInvariant()

        $documents += [pscustomobject]@{
            Index = $index + 1
            Parameter = $parameter
            CanonicalJson = $canonicalJson
            Bytes = $jsonBytes
            ContentHash = $contentHash
        }
        $index++
    }

    if ($documents.Count -eq 0) {
        return [pscustomobject]@{
            Arguments = $effectiveArguments
            RequiresValidation = $false
            StateRootPath = $null
            ArgumentRootPath = $null
            VolumePath = $null
            VolumeName = $null
            DirectoryEntries = @()
            FileEntries = @()
            DirectoryLocks = @()
            FileLocks = @()
        }
    }

    Initialize-AwsCliFileLockType
    try {
        $canonicalPath = [AraiaTask6FileLocksV2]::CanonicalizeStateRoot($StateRoot)
    }
    catch {
        throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
    }

    $argumentRoot = Join-Path $canonicalPath.FullPath 'aws-cli-json'
    $directoryEntries = [System.Collections.Generic.List[object]]::new()
    $fileEntries = [System.Collections.Generic.List[object]]::new()
    $argumentRootInspection = $null
    try {
        $volumeSerialNumber = $null
        foreach ($directoryPath in @($canonicalPath.DirectoryPaths)) {
            try {
                $directoryLock = [AraiaTask6FileLocksV2]::OpenDirectory($directoryPath)
            }
            catch {
                throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
            }
            if ($null -eq $volumeSerialNumber) {
                $volumeSerialNumber = $directoryLock.VolumeSerialNumber
            }
            elseif ($directoryLock.VolumeSerialNumber -ne $volumeSerialNumber) {
                $directoryLock.Dispose()
                throw 'DirectoryChainVolumeMismatch: Um componente pertence a outro volume.'
            }
            $directoryEntries.Add([pscustomobject]@{
                Path = $directoryPath
                Type = $directoryLock.ObjectType
                Attributes = $directoryLock.FileAttributes
                Volume = $directoryLock.VolumeSerialNumber
                FileId = $directoryLock.FileId
                ExpectedHash = $null
                Handle = $directoryLock.Handle
                Stream = $null
                Lock = $directoryLock
            })
        }

        $chainDirectoryPaths = @($canonicalPath.DirectoryPaths)
        $ancestorStartIndex = [Math]::Max(1, $chainDirectoryPaths.Count - 3)
        $ancestorEndIndex = $chainDirectoryPaths.Count - 2
        for ($index = $ancestorStartIndex; $index -le $ancestorEndIndex; $index++) {
            Assert-AncestorDirectoryAcl -Path $chainDirectoryPaths[$index]
        }
        Assert-RestrictedDirectoryAcl -Path $canonicalPath.FullPath
        $argumentRootCreated = -not (Test-Path -LiteralPath $argumentRoot)
        if ($argumentRootCreated) {
            [System.IO.Directory]::CreateDirectory($argumentRoot) | Out-Null
        }
        try {
            $argumentRootInspection = `
                [AraiaTask6FileLocksV2]::InspectDirectory($argumentRoot)
        }
        catch {
            throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
        }
        if ($argumentRootInspection.VolumeSerialNumber -ne $volumeSerialNumber) {
            $argumentRootInspection.Dispose()
            $argumentRootInspection = $null
            throw 'DirectoryChainVolumeMismatch: O diretório de argumentos pertence a outro volume.'
        }
        if ($argumentRootCreated) {
            $principal = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            & icacls $argumentRoot '/inheritance:r' "/grant:r" `
                "${principal}:(OI)(CI)F" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw 'Não foi possível restringir a ACL dos argumentos do AWS CLI.'
            }
        }
        Assert-RestrictedDirectoryAcl -Path $argumentRoot

        $argumentRootFull = [System.IO.Path]::GetFullPath($argumentRoot).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        ) + [System.IO.Path]::DirectorySeparatorChar
        $lockedPaths = @{}
        foreach ($document in $documents) {
            $parameterName = $document.Parameter.TrimStart('-')
            $argumentPath = Join-Path $argumentRoot `
                "$parameterName-$($document.ContentHash).json"
            $argumentPathFull = [System.IO.Path]::GetFullPath($argumentPath)
            if (-not $argumentPathFull.StartsWith(
                $argumentRootFull,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw 'O caminho do argumento JSON escapou do diretório restrito.'
            }

            if (-not (Test-Path -LiteralPath $argumentPathFull -PathType Leaf)) {
                $temporaryPath = "$argumentPathFull.$([guid]::NewGuid().ToString('N')).tmp"
                try {
                    [System.IO.File]::WriteAllText(
                        $temporaryPath,
                        $document.CanonicalJson,
                        [System.Text.UTF8Encoding]::new($false)
                    )
                    Move-Item -LiteralPath $temporaryPath -Destination $argumentPathFull
                }
                finally {
                    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
                        Remove-Item -LiteralPath $temporaryPath -Force
                    }
                }
            }

            if (-not $lockedPaths.ContainsKey($argumentPathFull)) {
                try {
                    $fileLock = [AraiaTask6FileLocksV2]::OpenFile($argumentPathFull)
                }
                catch {
                    throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
                }
                if ($fileLock.VolumeSerialNumber -ne $volumeSerialNumber) {
                    $fileLock.Dispose()
                    throw 'DirectoryChainVolumeMismatch: O arquivo pertence a outro volume.'
                }
                $observedHash = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($fileLock.Stream)
                ).ToLowerInvariant()
                $fileLock.Stream.Position = 0
                if ($observedHash -ne $document.ContentHash) {
                    $fileLock.Dispose()
                    throw "O arquivo persistido para $($document.Parameter) divergiu do conteúdo esperado."
                }
                $fileEntries.Add([pscustomobject]@{
                    Path = $argumentPathFull
                    Type = $fileLock.ObjectType
                    Attributes = $fileLock.FileAttributes
                    Volume = $fileLock.VolumeSerialNumber
                    FileId = $fileLock.FileId
                    ExpectedHash = $document.ContentHash
                    Handle = $fileLock.Handle
                    Stream = $fileLock.Stream
                    Lock = $fileLock
                })
                $lockedPaths[$argumentPathFull] = $true
            }

            $effectiveArguments[$document.Index] = `
                "file://$($argumentPathFull.Replace('\', '/'))"
        }

        try {
            $argumentRootLock = [AraiaTask6FileLocksV2]::OpenDirectory($argumentRoot)
        }
        catch {
            throw (Get-AwsCliInnermostExceptionMessage -Exception $_.Exception)
        }
        if ($argumentRootLock.ObjectType -ne $argumentRootInspection.ObjectType -or
            $argumentRootLock.VolumeSerialNumber -ne `
                $argumentRootInspection.VolumeSerialNumber -or
            $argumentRootLock.FileId -ne $argumentRootInspection.FileId) {
            $argumentRootLock.Dispose()
            throw 'DirectoryChainIdentityMismatch: O diretório de argumentos foi substituído.'
        }
        $directoryEntries.Add([pscustomobject]@{
            Path = $argumentRoot
            Type = $argumentRootLock.ObjectType
            Attributes = $argumentRootLock.FileAttributes
            Volume = $argumentRootLock.VolumeSerialNumber
            FileId = $argumentRootLock.FileId
            ExpectedHash = $null
            Handle = $argumentRootLock.Handle
            Stream = $null
            Lock = $argumentRootLock
        })
        $argumentRootInspection.Dispose()
        $argumentRootInspection = $null

        [pscustomobject]@{
            Arguments = $effectiveArguments
            RequiresValidation = $true
            StateRootPath = $canonicalPath.FullPath
            ArgumentRootPath = $argumentRoot
            VolumePath = $canonicalPath.VolumePath
            VolumeName = $canonicalPath.VolumeName
            DirectoryEntries = @($directoryEntries)
            FileEntries = @($fileEntries)
            DirectoryLocks = @($directoryEntries | ForEach-Object Handle)
            FileLocks = @($fileEntries | ForEach-Object Stream)
        }
    }
    catch {
        if ($null -ne $argumentRootInspection) {
            try { $argumentRootInspection.Dispose() } catch { }
        }
        Close-AwsCliArgumentLease -Lease ([pscustomobject]@{
            DirectoryEntries = @($directoryEntries)
            FileEntries = @($fileEntries)
        })
        throw
    }
}

function Get-AwsErrorCode {
    param([AllowEmptyString()][string] $Output)

    if ($Output -match '(?i)An error occurred \(([^)]+)\)') { return $Matches[1] }
    if ($Output -match '(?im)^\s*([A-Za-z][A-Za-z0-9.]+Exception)\s*:') { return $Matches[1] }
    $null
}

function Invoke-ProfileAws {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    try {
        $argumentLease = New-AwsCliArgumentLease -Arguments $Arguments
    }
    catch {
        return [pscustomobject]@{
            ExitCode = 252
            Output = "LocalArgumentPreparationFailure: $($_.Exception.Message)"
        }
    }
    try {
        $effectiveArguments = @($argumentLease.Arguments)
        try {
            Assert-AwsCliArgumentLeaseCurrent -Lease $argumentLease
        }
        catch {
            return [pscustomobject]@{
                ExitCode = 252
                Output = "LocalArgumentPreparationFailure: $($_.Exception.Message)"
            }
        }
        try {
            $lines = @(& aws @effectiveArguments 2>&1)
            $exitCode = $LASTEXITCODE
            $output = ($lines | ForEach-Object { $_.ToString() }) -join `
                [Environment]::NewLine
        }
        catch {
            $exitCode = 255
            $output = "LocalProcessInvocationFailure: $($_.Exception.Message)"
        }
        [pscustomobject]@{
            ExitCode = $exitCode
            Output = $output
        }
    }
    finally {
        Close-AwsCliArgumentLease -Lease $argumentLease
    }
}

function Invoke-ProfileAwsSingleAttempt {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $savedMaxAttempts = $env:AWS_MAX_ATTEMPTS
    try {
        $env:AWS_MAX_ATTEMPTS = '1'
        Invoke-ProfileAws -Arguments $Arguments
    }
    finally {
        if ($null -eq $savedMaxAttempts) {
            Remove-Item -LiteralPath 'Env:AWS_MAX_ATTEMPTS' -ErrorAction SilentlyContinue
        }
        else {
            $env:AWS_MAX_ATTEMPTS = $savedMaxAttempts
        }
    }
}

function Invoke-AwsSingleAttempt {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [switch] $AllowFailure
    )

    $savedMaxAttempts = $env:AWS_MAX_ATTEMPTS
    try {
        $env:AWS_MAX_ATTEMPTS = '1'
        Invoke-Aws -Credential $Credential -Arguments $Arguments -AllowFailure:$AllowFailure
    }
    finally {
        if ($null -eq $savedMaxAttempts) {
            Remove-Item -LiteralPath 'Env:AWS_MAX_ATTEMPTS' -ErrorAction SilentlyContinue
        }
        else {
            $env:AWS_MAX_ATTEMPTS = $savedMaxAttempts
        }
    }
}

function Get-FailureDisposition {
    param(
        [AllowEmptyString()][string] $Output,
        [AllowNull()][Nullable[int]] $ExitCode
    )

    $errorCode = Get-AwsErrorCode -Output $Output
    if ($ExitCode -eq 252) {
        return 'failed-definitive'
    }
    $definitiveCodes = @(
        'AccessDenied', 'AccessDeniedException', 'UnauthorizedOperation',
        'ValidationError', 'ValidationException', 'InvalidParameter',
        'InvalidParameterException', 'InvalidParameterValue',
        'InvalidParameterValueException', 'MalformedPolicyDocument',
        'MalformedPolicyDocumentException', 'EntityAlreadyExists',
        'EntityAlreadyExistsException', 'AlreadyExistsException',
        'BucketAlreadyExists', 'BucketAlreadyOwnedByYou',
        'TrailAlreadyExistsException', 'NoSuchEntity', 'NoSuchEntityException',
        'NoSuchBucket', 'NoSuchKey', 'NotFound', 'NotFoundException', '404',
        'LimitExceeded', 'LimitExceededException', 'QuotaExceededException'
    )
    if ($errorCode -in $definitiveCodes) { return 'failed-definitive' }
    'indeterminate'
}

function Write-Journal {
    param(
        [Parameter(Mandatory = $true)][string] $Type,
        [Parameter(Mandatory = $true)][string] $ResourceType,
        [Parameter(Mandatory = $true)][string] $ResourceId,
        [Parameter(Mandatory = $true)][string] $Status
    )

    $entry = [ordered]@{
        Ts = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
        Type = $Type
        ResourceType = $ResourceType
        ResourceId = $ResourceId
        Status = $Status
        RunId = $RunId
    } | ConvertTo-Json -Compress
    Add-Content -LiteralPath $JournalPath -Value $entry -Encoding utf8
}

function Invoke-Aws {
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Credential,
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,
        [switch] $AllowFailure
    )

    $effectiveArguments = @($Arguments)
    if ($effectiveArguments.Count -ge 2 -and
        $effectiveArguments[0] -eq 's3api' -and
        $effectiveArguments[1] -ne 'create-bucket' -and
        $effectiveArguments -notcontains '--expected-bucket-owner') {
        $effectiveArguments += @('--expected-bucket-owner', $ExpectedAccountId)
    }
    try {
        $argumentLease = New-AwsCliArgumentLease -Arguments $effectiveArguments
    }
    catch {
        $preparationResult = [pscustomobject]@{
            ExitCode = 252
            Output = "LocalArgumentPreparationFailure: $($_.Exception.Message)"
        }
        if (-not $AllowFailure) {
            throw "O AWS CLI não foi iniciado: $($preparationResult.Output)"
        }
        return $preparationResult
    }

    $savedAccessKey = $env:AWS_ACCESS_KEY_ID
    $savedSecretKey = $env:AWS_SECRET_ACCESS_KEY
    $savedSessionToken = $env:AWS_SESSION_TOKEN
    $savedProfile = $env:AWS_PROFILE
    $savedDefaultProfile = $env:AWS_DEFAULT_PROFILE
    $savedRegion = $env:AWS_REGION
    $savedDefaultRegion = $env:AWS_DEFAULT_REGION

    try {
        $env:AWS_ACCESS_KEY_ID = $Credential.AccessKeyId
        $env:AWS_SECRET_ACCESS_KEY = $Credential.SecretAccessKey
        $env:AWS_SESSION_TOKEN = $Credential.SessionToken
        $env:AWS_REGION = $Region
        $env:AWS_DEFAULT_REGION = $Region
        Remove-Item Env:AWS_PROFILE -ErrorAction SilentlyContinue
        Remove-Item Env:AWS_DEFAULT_PROFILE -ErrorAction SilentlyContinue

        $effectiveArguments = @($argumentLease.Arguments)
        try {
            Assert-AwsCliArgumentLeaseCurrent -Lease $argumentLease
        }
        catch {
            $preparationResult = [pscustomobject]@{
                ExitCode = 252
                Output = "LocalArgumentPreparationFailure: $($_.Exception.Message)"
            }
            if (-not $AllowFailure) {
                throw "O AWS CLI não foi iniciado: $($preparationResult.Output)"
            }
            return $preparationResult
        }
        try {
            $lines = @(& aws @effectiveArguments 2>&1)
            $exitCode = $LASTEXITCODE
            $output = ($lines | ForEach-Object { $_.ToString() }) -join `
                [Environment]::NewLine
        }
        catch {
            $exitCode = 255
            $output = "LocalProcessInvocationFailure: $($_.Exception.Message)"
        }
    }
    finally {
        $env:AWS_ACCESS_KEY_ID = $savedAccessKey
        $env:AWS_SECRET_ACCESS_KEY = $savedSecretKey
        $env:AWS_SESSION_TOKEN = $savedSessionToken
        $env:AWS_PROFILE = $savedProfile
        $env:AWS_DEFAULT_PROFILE = $savedDefaultProfile
        $env:AWS_REGION = $savedRegion
        $env:AWS_DEFAULT_REGION = $savedDefaultRegion
        Close-AwsCliArgumentLease -Lease $argumentLease
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "O AWS CLI falhou ($exitCode): aws $($argumentLease.Arguments -join ' ')`n$output"
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-AwsJson {
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Credential,
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $result = Invoke-Aws -Credential $Credential -Arguments $Arguments
    if ([string]::IsNullOrWhiteSpace($result.Output)) {
        return $null
    }

    $result.Output | ConvertFrom-Json -DateKind String
}

function Get-OperatorCredential {
    param([Parameter(Mandatory = $true)][string] $RoleArn)

    $response = aws sts assume-role `
        --profile $Profile `
        --role-arn $RoleArn `
        --role-session-name task6-matrix `
        --duration-seconds 3600 `
        --output json | ConvertFrom-Json

    if ($LASTEXITCODE -ne 0 -or -not $response.Credentials) {
        throw 'Não foi possível assumir a role temporária do experimento.'
    }

    $response.Credentials
}

function Get-DataCredential {
    param(
        [Parameter(Mandatory = $true)][psobject] $OperatorCredential,
        [Parameter(Mandatory = $true)][string] $RoleArn,
        [Parameter(Mandatory = $true)][string] $SessionName
    )

    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $result = Invoke-Aws -Credential $OperatorCredential -AllowFailure -Arguments @(
            'sts', 'assume-role', '--role-arn', $RoleArn,
            '--role-session-name', $SessionName, '--duration-seconds', '3600',
            '--output', 'json'
        )
        if ($result.ExitCode -eq 0) {
            return ($result.Output | ConvertFrom-Json).Credentials
        }
        Start-Sleep -Seconds 5
    }

    throw "A propagação não permitiu assumir $RoleArn."
}

function Save-State {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $State)

    $State.UpdatedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    $payload = $State | ConvertTo-Json -Depth 30 -Compress
    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $key = Get-StateIntegrityKey -Create
    $hmac = $null
    $operationError = $null
    try {
        $hmac = [System.Security.Cryptography.HMACSHA256]::new($key)
        $mac = $hmac.ComputeHash($payloadBytes)
    }
    catch {
        $operationError = $_
        throw
    }
    finally {
        try {
            if ($null -ne $hmac) { $hmac.Dispose() }
        }
        catch {
            if ($null -eq $operationError) { throw }
        }
        finally {
            [AraiaTask6CryptographicOperations]::ZeroMemory($key)
        }
    }
    $envelope = [ordered]@{
        Algorithm = 'HMACSHA256-DPAPI-CurrentUser'
        Payload = [Convert]::ToBase64String($payloadBytes)
        Mac = [Convert]::ToHexString($mac).ToLowerInvariant()
    }
    $temporaryPath = "$StatePath.tmp"
    $envelope | ConvertTo-Json -Compress |
        Set-Content -LiteralPath $temporaryPath -Encoding utf8
    Move-Item -LiteralPath $temporaryPath -Destination $StatePath -Force
}

function Read-State {
    Assert-StateStorageAcl
    if (-not (Test-Path -LiteralPath $StatePath)) {
        throw 'Estado restrito da execução não encontrado.'
    }
    $envelope = Get-Content -Raw -LiteralPath $StatePath |
        ConvertFrom-Json -AsHashtable -DateKind String
    if ($envelope.Algorithm -ne 'HMACSHA256-DPAPI-CurrentUser') {
        throw 'O algoritmo de integridade do estado não é o aprovado.'
    }
    $payloadBytes = [Convert]::FromBase64String($envelope.Payload)
    $observedMac = [Convert]::FromHexString($envelope.Mac)
    $key = Get-StateIntegrityKey
    $hmac = $null
    $operationError = $null
    try {
        $hmac = [System.Security.Cryptography.HMACSHA256]::new($key)
        $expectedMac = $hmac.ComputeHash($payloadBytes)
    }
    catch {
        $operationError = $_
        throw
    }
    finally {
        try {
            if ($null -ne $hmac) { $hmac.Dispose() }
        }
        catch {
            if ($null -eq $operationError) { throw }
        }
        finally {
            [AraiaTask6CryptographicOperations]::ZeroMemory($key)
        }
    }
    if (-not [AraiaTask6CryptographicOperations]::FixedTimeEquals(
        $observedMac,
        $expectedMac
    )) {
        throw 'A integridade autenticada do estado falhou.'
    }
    $state = [System.Text.Encoding]::UTF8.GetString($payloadBytes) |
        ConvertFrom-Json -AsHashtable -DateKind String
    if ($state.SchemaVersion -eq 5) {
        foreach ($mutation in @($state.Mutations)) {
            if (-not $mutation.Contains('EventTime')) { $mutation.EventTime = $null }
            foreach ($attempt in @($mutation.Attempts)) {
                if (-not $attempt.Contains('EventTime')) { $attempt.EventTime = $null }
            }
        }
        foreach ($expected in @($state.ExpectedEvents)) {
            if (-not $expected.Contains('EventTime')) { $expected.EventTime = $null }
            if (-not $expected.Contains('EventId')) { $expected.EventId = $null }
        }
        foreach ($expected in @($state.Cleanup.ExpectedFinalEvents)) {
            if (-not $expected.Contains('EventTime')) { $expected.EventTime = $null }
            if (-not $expected.Contains('EventId')) { $expected.EventId = $null }
        }
        $state.SchemaVersion = 6
        Save-State -State $state
    }
    elseif ($state.SchemaVersion -ne 6) {
        throw "A versão do estado restrito não é compatível: $($state.SchemaVersion)."
    }
    $state
}

function Get-Mutation {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId
    )

    $matches = @($State.Mutations | Where-Object OperationId -eq $OperationId |
        Select-Object -First 1)
    if ($matches.Count -eq 0) { return $null }
    $matches[0]
}

function Start-MutationIntent {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][string] $ActorRoleName,
        [Parameter(Mandatory = $true)][string] $EventSource,
        [Parameter(Mandatory = $true)][string] $EventName,
        [Parameter(Mandatory = $true)][string[]] $ResourceTokens
    )

    $existing = Get-Mutation -State $State -OperationId $OperationId
    if ($existing) {
        if ($existing.Status -notin @('intent', 'succeeded', 'reconciled', 'not-applied')) {
            throw "A mutação $OperationId possui estado inválido: $($existing.Status)."
        }
        if (-not $existing.Contains('Attempts')) {
            $existing.Attempts = @()
            Save-State -State $State
        }
        return $existing
    }

    $mutation = [ordered]@{
        OperationId = $OperationId
        ActorRoleName = $ActorRoleName
        EventSource = $EventSource
        EventName = $EventName
        ResourceTokens = $ResourceTokens
        StartedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
        CompletedAt = $null
        EventTime = $null
        EventId = $null
        Status = 'intent'
        Attempts = @()
    }
    $State.Mutations = @($State.Mutations) + $mutation
    Save-State -State $State
    $mutation
}

function Start-MutationAttempt {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId
    )

    $mutation = Get-Mutation -State $State -OperationId $OperationId
    if (-not $mutation) {
        throw "A tentativa de $OperationId não possui intenção autenticada."
    }
    if (-not $mutation.Contains('Attempts')) { $mutation.Attempts = @() }
    if ($mutation.Status -in @('succeeded', 'reconciled')) {
        throw "A mutação $OperationId já foi concluída e não aceita nova tentativa."
    }
    $sequence = @($mutation.Attempts).Count + 1
    $attempt = [ordered]@{
        AttemptId = '{0}:attempt-{1:d2}' -f $OperationId, $sequence
        Sequence = $sequence
        StartedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
        CompletedAt = $null
        LocalOutcome = 'in-flight'
        ExitCode = $null
        ErrorCode = $null
        EventTime = $null
        EventId = $null
    }
    $mutation.Status = 'intent'
    $mutation.CompletedAt = $null
    $mutation.EventTime = $null
    $mutation.EventId = $null
    $mutation.Attempts = @($mutation.Attempts) + $attempt
    Save-State -State $State
    $attempt
}

function Complete-MutationAttempt {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][string] $AttemptId,
        [ValidateSet('succeeded', 'failed-definitive', 'indeterminate', 'reconciled')]
        [Parameter(Mandatory = $true)][string] $LocalOutcome,
        [AllowNull()][Nullable[int]] $ExitCode,
        [AllowNull()][string] $ErrorCode,
        [AllowNull()][string] $EventTime,
        [AllowNull()][string] $EventId,
        [AllowNull()][string] $CompletedAt
    )

    $mutation = Get-Mutation -State $State -OperationId $OperationId
    $attempt = @($mutation.Attempts | Where-Object AttemptId -eq $AttemptId |
        Select-Object -First 1)[0]
    if (-not $attempt) { throw "A tentativa autenticada $AttemptId não existe." }
    $attempt.LocalOutcome = $LocalOutcome
    $attempt.CompletedAt = if ($CompletedAt) {
        $CompletedAt
    }
    else {
        ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    }
    $attempt.ExitCode = $ExitCode
    $attempt.ErrorCode = $ErrorCode
    if ($EventTime) { $attempt.EventTime = $EventTime }
    if ($EventId) { $attempt.EventId = $EventId }
    Save-State -State $State
}

function Test-AllMutationAttemptsDefinitelyFailed {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId
    )

    $mutation = Get-Mutation -State $State -OperationId $OperationId
    $attempts = @($mutation.Attempts)
    $attempts.Count -gt 0 -and
        @($attempts | Where-Object LocalOutcome -ne 'failed-definitive').Count -eq 0
}

function Resolve-LocalCliValidationFailureAttempts {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId
    )

    $mutation = Get-Mutation -State $State -OperationId $OperationId
    $reclassified = $false
    foreach ($attempt in @($mutation.Attempts | Where-Object {
        $_.LocalOutcome -eq 'indeterminate' -and
        $_.ExitCode -eq 252 -and
        [string]::IsNullOrWhiteSpace([string]$_.EventTime) -and
        [string]::IsNullOrWhiteSpace([string]$_.EventId) -and
        -not [string]::IsNullOrWhiteSpace([string]$_.StartedAt) -and
        -not [string]::IsNullOrWhiteSpace([string]$_.CompletedAt)
    })) {
        $attempt.LocalOutcome = 'failed-definitive'
        $reclassified = $true
    }
    if ($reclassified) { Save-State -State $State }
}

function Get-MonotonicCompletionTimestamp {
    param(
        [Parameter(Mandatory = $true)][string] $StartedAt,
        [AllowNull()][string] $CompletedAt,
        [AllowNull()][string] $ObservedAt
    )

    $started = ConvertFrom-IsoTimestamp -Value $StartedAt
    $completed = if ($CompletedAt) {
        ConvertFrom-IsoTimestamp -Value $CompletedAt
    }
    elseif ($ObservedAt) {
        ConvertFrom-IsoTimestamp -Value $ObservedAt
    }
    else { [System.DateTimeOffset]::UtcNow }
    if ($completed -lt $started) { $completed = $started }
    ConvertTo-CanonicalUtcTimestamp -Value $completed
}

function Complete-Mutation {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [ValidateSet('succeeded', 'reconciled', 'not-applied')]
        [string] $Status = 'succeeded',
        [AllowNull()][string] $EventTime,
        [AllowNull()][string] $EventId,
        [AllowNull()][string] $CompletedAt
    )

    $mutation = Get-Mutation -State $State -OperationId $OperationId
    if (-not $mutation) {
        throw "A mutação $OperationId não possui intenção autenticada."
    }
    $mutation.Status = $Status
    $mutation.CompletedAt = if ($CompletedAt) {
        $CompletedAt
    }
    else {
        ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    }
    if ($EventTime) { $mutation.EventTime = $EventTime }
    if ($EventId) { $mutation.EventId = $EventId }
    Save-State -State $State
}

function Invoke-TrackedProfileMutation {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][string] $EventSource,
        [Parameter(Mandatory = $true)][string] $EventName,
        [Parameter(Mandatory = $true)][string[]] $ResourceTokens,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $intent = Start-MutationIntent -State $State -OperationId $OperationId `
        -ActorRoleName $adminRoleName -EventSource $EventSource -EventName $EventName `
        -ResourceTokens $ResourceTokens
    if ($intent.Status -in @('succeeded', 'reconciled')) {
        throw "A mutação $OperationId já foi concluída e não pode ser repetida."
    }
    $attempt = Start-MutationAttempt -State $State -OperationId $OperationId
    $result = Invoke-ProfileAwsSingleAttempt -Arguments $Arguments
    $errorCode = Get-AwsErrorCode -Output $result.Output
    if ($result.ExitCode -ne 0) {
        $failureDisposition = Get-FailureDisposition -Output $result.Output `
            -ExitCode $result.ExitCode
        Complete-MutationAttempt -State $State -OperationId $OperationId `
            -AttemptId $attempt.AttemptId `
            -LocalOutcome $failureDisposition `
            -ExitCode $result.ExitCode -ErrorCode $errorCode
        if (Test-AllMutationAttemptsDefinitelyFailed -State $State `
            -OperationId $OperationId) {
            Complete-Mutation -State $State -OperationId $OperationId `
                -Status 'not-applied'
        }
        throw "A mutação federada $OperationId falhou: $($result.Output)"
    }
    Complete-MutationAttempt -State $State -OperationId $OperationId `
        -AttemptId $attempt.AttemptId -LocalOutcome 'succeeded' `
        -ExitCode $result.ExitCode -ErrorCode $null
    Complete-Mutation -State $State -OperationId $OperationId
    $result
}

function Get-CompatibleMutationAttempt {
    param(
        [Parameter(Mandatory = $true)][object[]] $Attempts,
        [Parameter(Mandatory = $true)][System.DateTimeOffset] $EventTime,
        [ValidateSet('success', 'failure')][string] $Outcome = 'success'
    )

    $eligible = if ($Outcome -eq 'failure') {
        @($Attempts | Where-Object LocalOutcome -eq 'failed-definitive')
    }
    else {
        @($Attempts | Where-Object LocalOutcome -ne 'failed-definitive')
    }
    $matches = @($eligible | Where-Object {
        $attemptStart = (ConvertFrom-IsoTimestamp -Value $_.StartedAt).AddMinutes(-2)
        $attemptEnd = if ($_.CompletedAt) {
            (ConvertFrom-IsoTimestamp -Value $_.CompletedAt).AddMinutes(2)
        }
        else {
            (ConvertFrom-IsoTimestamp -Value $_.StartedAt).AddMinutes(10)
        }
        $EventTime -ge $attemptStart -and $EventTime -le $attemptEnd
    })
    if ($matches.Count -eq 0) { return $null }

    $preceding = @($matches | Where-Object {
        (ConvertFrom-IsoTimestamp -Value $_.StartedAt) -le $EventTime
    } | Sort-Object StartedAt -Descending)
    if ($preceding.Count -eq 1) { return $preceding[0] }
    if ($preceding.Count -gt 1) { return $null }
    if ($matches.Count -eq 1) { return $matches[0] }
    $null
}

function Find-MutationEvent {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [ValidateSet('success', 'failure')][string] $Outcome = 'success'
    )

    $mutation = Get-Mutation -State $State -OperationId $OperationId
    if (-not $mutation) { return $null }
    $attempts = @($mutation.Attempts)
    if ($attempts.Count -eq 0) {
        $attempts = @([ordered]@{
            AttemptId = $null
            StartedAt = $mutation.StartedAt
            CompletedAt = $mutation.CompletedAt
            LocalOutcome = if ($mutation.Status -eq 'not-applied') {
                'failed-definitive'
            }
            else { 'in-flight' }
        })
    }
    $eligibleAttempts = if ($Outcome -eq 'failure') {
        @($attempts | Where-Object LocalOutcome -eq 'failed-definitive')
    }
    else {
        @($attempts | Where-Object LocalOutcome -ne 'failed-definitive')
    }
    if ($eligibleAttempts.Count -eq 0) { return $null }
    $start = @($eligibleAttempts | ForEach-Object {
        (ConvertFrom-IsoTimestamp -Value $_.StartedAt).AddMinutes(-2)
    } | Sort-Object | Select-Object -First 1)[0]
    $end = @($eligibleAttempts | ForEach-Object {
        if ($_.CompletedAt) {
            (ConvertFrom-IsoTimestamp -Value $_.CompletedAt).AddMinutes(2)
        }
        else {
            (ConvertFrom-IsoTimestamp -Value $_.StartedAt).AddMinutes(10)
        }
    } | Sort-Object | Select-Object -Last 1)[0]
    if ($end -gt [System.DateTimeOffset]::UtcNow) {
        $end = [System.DateTimeOffset]::UtcNow
    }
    $historyResult = Invoke-ProfileAws -Arguments @(
        'cloudtrail', 'lookup-events', '--profile', $Profile, '--region', $Region,
        '--start-time', (ConvertTo-CanonicalUtcTimestamp -Value $start),
        '--end-time', (ConvertTo-CanonicalUtcTimestamp -Value $end), '--output', 'json'
    )
    if ($historyResult.ExitCode -ne 0) {
        throw "Não foi possível reconciliar $OperationId no Event History: $($historyResult.Output)"
    }
    $history = $historyResult.Output | ConvertFrom-Json -DateKind String
    $candidates = @()
    foreach ($historyEvent in @($history.Events)) {
        $document = $historyEvent.CloudTrailEvent | ConvertFrom-Json
        $documentText = $historyEvent.CloudTrailEvent
        $eventTime = ConvertFrom-IsoTimestamp -Value $historyEvent.EventTime
        $eventIdMatches = -not $mutation.EventId -or
            $historyEvent.EventId -eq $mutation.EventId
        $matchedAttempt = if ($mutation.EventId) {
            $eventAttempts = @($eligibleAttempts | Where-Object EventId -eq $mutation.EventId)
            if ($eventAttempts.Count -eq 1) { $eventAttempts[0] } else { $null }
        }
        else {
            Get-CompatibleMutationAttempt -Attempts $eligibleAttempts `
                -EventTime $eventTime -Outcome $Outcome
        }
        $actorMatches = $document.userIdentity.arn -like "*/$($mutation.ActorRoleName)/*" -or
            $document.userIdentity.sessionContext.sessionIssuer.userName -eq $mutation.ActorRoleName
        $resourceMatches = $true
        foreach ($token in @($mutation.ResourceTokens)) {
            if (-not $documentText.Contains($token, [StringComparison]::Ordinal)) {
                $resourceMatches = $false
                break
            }
        }
        $outcomeMatches = if ($Outcome -eq 'success') {
            [string]::IsNullOrWhiteSpace($document.errorCode)
        }
        else {
            -not [string]::IsNullOrWhiteSpace($document.errorCode)
        }
        if ($historyEvent.EventSource -eq $mutation.EventSource -and
            $historyEvent.EventName -eq $mutation.EventName -and $actorMatches -and
            $outcomeMatches -and $resourceMatches -and $eventIdMatches -and $matchedAttempt) {
            $historyEvent | Add-Member -NotePropertyName AraiaAttemptId `
                -NotePropertyValue $matchedAttempt.AttemptId -Force
            $candidates += $historyEvent
        }
    }
    $candidates = @($candidates | Group-Object EventId | ForEach-Object {
        $_.Group[0]
    })
    $matchedEvent = if ($candidates.Count -eq 1) { $candidates[0] } else { $null }
    $matchedEvent
}

function Test-ExpectedTags {
    param(
        [Parameter(Mandatory = $true)][object[]] $Tags,
        [Parameter(Mandatory = $true)][string] $KeyName,
        [Parameter(Mandatory = $true)][string] $ValueName
    )

    $runTag = $Tags | Where-Object { $_.$KeyName -eq 'RunId' -and $_.$ValueName -eq $RunId }
    $taskTag = $Tags | Where-Object { $_.$KeyName -eq 'AraiaTask' -and $_.$ValueName -eq 'Task6' }
    [bool]$runTag -and [bool]$taskTag
}

function Remove-BucketVersions {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string] $Bucket,
        [Parameter(Mandatory = $true)][string] $ExpectedOwner,
        [string] $ProtectedVersionId,
        [switch] $AllowGovernanceBypass
    )

    $versions = Invoke-AwsJson -Credential $Credential -Arguments @(
        's3api', 'list-object-versions', '--bucket', $Bucket,
        '--expected-bucket-owner', $ExpectedOwner, '--output', 'json'
    )

    $blocked = @()
    foreach ($item in @($versions.Versions | Where-Object { $null -ne $_ })) {
        if ($ProtectedVersionId -and $item.VersionId -eq $ProtectedVersionId) {
            continue
        }
        $retentionResult = Invoke-Aws -Credential $Credential -AllowFailure -Arguments @(
            's3api', 'get-object-retention', '--bucket', $Bucket, '--key', $item.Key,
            '--version-id', $item.VersionId, '--expected-bucket-owner', $ExpectedOwner,
            '--output', 'json'
        )
        $deleteArguments = @(
            's3api', 'delete-object', '--bucket', $Bucket, '--key', $item.Key,
            '--version-id', $item.VersionId, '--expected-bucket-owner', $ExpectedOwner
        )
        if ($retentionResult.ExitCode -eq 0) {
            $retention = ($retentionResult.Output |
                ConvertFrom-Json -DateKind String).Retention
            $retainUntil = ConvertFrom-IsoTimestamp -Value $retention.RetainUntilDate
            if ($retainUntil -gt [System.DateTimeOffset]::UtcNow) {
                if ($retention.Mode -eq 'COMPLIANCE') {
                    $blocked += [ordered]@{ Key = $item.Key; VersionId = $item.VersionId; RetainUntil = $retainUntil }
                    continue
                }
                if ($retention.Mode -eq 'GOVERNANCE' -and $AllowGovernanceBypass) {
                    $deleteArguments += '--bypass-governance-retention'
                }
            }
        }

        Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupDeleteObjectVersion' `
            -Credential $Credential -ExpectSuccess $true -Arguments $deleteArguments | Out-Null
    }

    foreach ($item in @($versions.DeleteMarkers | Where-Object { $null -ne $_ })) {
        Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupDeleteMarker' `
            -Credential $Credential -ExpectSuccess $true -Arguments @(
                's3api', 'delete-object', '--bucket', $Bucket, '--key', $item.Key,
                '--version-id', $item.VersionId, '--expected-bucket-owner', $ExpectedOwner
            ) | Out-Null
    }

    $blocked
}

$caller = aws sts get-caller-identity --profile $Profile --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
    throw 'A sessão SSO não é válida.'
}
if ($caller.Arn -match ':root$') {
    throw 'A sessão administrativa não pode ser root.'
}
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
$roles = [ordered]@{
    UploadA = [ordered]@{ Name = $dataRoleNames[0]; Arn = $dataRoleArns[0] }
    UploadB = [ordered]@{ Name = $dataRoleNames[1]; Arn = $dataRoleArns[1] }
    ValidatorA = [ordered]@{ Name = $dataRoleNames[2]; Arn = $dataRoleArns[2] }
    DisposerA = [ordered]@{ Name = $dataRoleNames[3]; Arn = $dataRoleArns[3] }
    DispatchSynthetic = [ordered]@{ Name = $dataRoleNames[4]; Arn = $dataRoleArns[4] }
    CurrentVersionProbe = [ordered]@{ Name = $dataRoleNames[5]; Arn = $dataRoleArns[5] }
}

$expectedOperatorPolicy = [ordered]@{
    Version = '2012-10-17'
    Statement = @(
        [ordered]@{
            Sid = 'CallerIdentity'
            Effect = 'Allow'
            Action = 'sts:GetCallerIdentity'
            Resource = '*'
        },
        [ordered]@{
            Sid = 'ManageExactExperimentDataRoles'
            Effect = 'Allow'
            Action = @(
                'iam:DeleteRole', 'iam:GetRole', 'iam:DeleteRolePolicy',
                'iam:GetRolePolicy', 'iam:ListRolePolicies'
            )
            Resource = $dataRoleArns
        },
        [ordered]@{
            Sid = 'AssumeExactExperimentDataRoles'
            Effect = 'Allow'
            Action = 'sts:AssumeRole'
            Resource = $dataRoleArns
        },
        [ordered]@{
            Sid = 'ManageExactExperimentBuckets'
            Effect = 'Allow'
            Action = @(
                's3:DeleteBucket', 's3:GetBucketLocation',
                's3:GetBucketTagging', 's3:GetBucketVersioning',
                's3:GetBucketOwnershipControls', 's3:GetBucketPublicAccessBlock',
                's3:GetEncryptionConfiguration',
                's3:GetBucketObjectLockConfiguration', 's3:ListBucket',
                's3:ListBucketVersions', 's3:GetObject',
                's3:GetObjectVersion', 's3:DeleteObject', 's3:DeleteObjectVersion',
                's3:GetObjectRetention', 's3:BypassGovernanceRetention'
            )
            Resource = @(
                "arn:aws:s3:::$Prefix-obj",
                "arn:aws:s3:::$Prefix-obj/*",
                "arn:aws:s3:::$Prefix-ct",
                "arn:aws:s3:::$Prefix-ct/*"
            )
        },
        [ordered]@{
            Sid = 'DeleteExperimentAliases'
            Effect = 'Allow'
            Action = 'kms:DeleteAlias'
            Resource = "arn:aws:kms:$Region`:$accountId`:alias/$Prefix-*"
        },
        [ordered]@{
            Sid = 'ReadKmsInventory'
            Effect = 'Allow'
            Action = 'kms:ListAliases'
            Resource = '*'
        },
        [ordered]@{
            Sid = 'ManageExactExperimentTrail'
            Effect = 'Allow'
            Action = @(
                'cloudtrail:DeleteTrail',
                'cloudtrail:StopLogging',
                'cloudtrail:GetTrail', 'cloudtrail:GetTrailStatus', 'cloudtrail:GetEventSelectors',
                'cloudtrail:ListTags'
            )
            Resource = "arn:aws:cloudtrail:$Region`:$accountId`:trail/$Prefix-trail"
        },
        [ordered]@{
            Sid = 'ReadCloudTrailInventory'
            Effect = 'Allow'
            Action = @('cloudtrail:ListTrails', 'cloudtrail:DescribeTrails')
            Resource = '*'
        }
    )
}

$operatorCredential = $null
$operatorRoleAbsentForCleanup = $false
$operatorPolicyAbsentForCleanup = $false
$adminRoleName = ($caller.Arn -split '/')[1]
if ($Phase -ne 'VerifyCleanup') {
    $adminRoleResult = Invoke-ProfileAws -Arguments @(
        'iam', 'get-role', '--profile', $Profile, '--role-name', $adminRoleName, '--output', 'json'
    )
    if ($adminRoleResult.ExitCode -ne 0) {
        throw "Não foi possível resolver a role administrativa do IAM Identity Center: $($adminRoleResult.Output)"
    }
    $adminRole = $adminRoleResult.Output | ConvertFrom-Json
    $expectedOperatorTrustPolicy = [ordered]@{
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
    $operatorRoleResult = Invoke-ProfileAws -Arguments @(
        'iam', 'get-role', '--profile', $Profile, '--role-name', $OperatorRoleName, '--output', 'json'
    )
    if ($operatorRoleResult.ExitCode -eq 0) {
        $operatorRole = $operatorRoleResult.Output | ConvertFrom-Json
        if (-not (Test-ExpectedTags -Tags $operatorRole.Role.Tags -KeyName 'Key' -ValueName 'Value')) {
            throw 'As tags da role temporária não correspondem ao RunId autorizado.'
        }
        if ((ConvertTo-CanonicalJson -Value $operatorRole.Role.AssumeRolePolicyDocument) -ne
            (ConvertTo-CanonicalJson -Value $expectedOperatorTrustPolicy) -or
            $operatorRole.Role.MaxSessionDuration -ne 3600 -or $operatorRole.Role.Path -ne '/') {
            throw 'A trust policy, o path ou a duração da role temporária divergiu do envelope aprovado.'
        }
        $managedPoliciesResult = Invoke-ProfileAws -Arguments @(
            'iam', 'list-attached-role-policies', '--profile', $Profile,
            '--role-name', $OperatorRoleName, '--output', 'json'
        )
        if ($managedPoliciesResult.ExitCode -ne 0) {
            throw "Não foi possível listar as managed policies da role temporária: $($managedPoliciesResult.Output)"
        }
        $managedPolicies = $managedPoliciesResult.Output | ConvertFrom-Json
        if ($managedPolicies.AttachedPolicies.Count -ne 0) {
            throw 'A role temporária não pode possuir managed policies.'
        }
        $inlinePoliciesResult = Invoke-ProfileAws -Arguments @(
            'iam', 'list-role-policies', '--profile', $Profile,
            '--role-name', $OperatorRoleName, '--output', 'json'
        )
        if ($inlinePoliciesResult.ExitCode -ne 0) {
            throw "Não foi possível listar as inline policies da role temporária: $($inlinePoliciesResult.Output)"
        }
        $inlinePolicies = $inlinePoliciesResult.Output | ConvertFrom-Json
        if ($inlinePolicies.PolicyNames.Count -eq 0 -and $Phase -eq 'Cleanup' -and
            (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
            $resumeState = Read-State
            $deletePolicyIntent = Get-Mutation -State $resumeState `
                -OperationId 'final-delete-operator-policy'
            if ($resumeState.Status -eq 'cleanup-finalizing' -and
                $resumeState.Cleanup.Status -eq 'finalizing' -and $deletePolicyIntent -and
                -not $resumeState.Created.Trail -and -not $resumeState.Created.TrailBucket -and
                -not $resumeState.Created.ObjectBucket -and
                @($resumeState.Created.RoleNames).Count -eq 0 -and
                @($resumeState.Created.AliasNames).Count -eq 0) {
                $operatorPolicyAbsentForCleanup = $true
            }
            else {
                throw 'A policy temporária está ausente antes da fronteira final autenticada do cleanup.'
            }
        }
        elseif ($inlinePolicies.PolicyNames.Count -ne 1 -or
            $inlinePolicies.PolicyNames[0] -ne 'ExperimentOperatorPolicy') {
            throw 'A role temporária deve possuir somente ExperimentOperatorPolicy.'
        }
        if (-not $operatorPolicyAbsentForCleanup) {
            $observedPolicyResult = Invoke-ProfileAws -Arguments @(
                'iam', 'get-role-policy', '--profile', $Profile, '--role-name', $OperatorRoleName,
                '--policy-name', 'ExperimentOperatorPolicy', '--query', 'PolicyDocument', '--output', 'json'
            )
            if ($observedPolicyResult.ExitCode -ne 0) {
                throw "Não foi possível ler a política efetiva da role temporária: $($observedPolicyResult.Output)"
            }
            $observedOperatorPolicy = $observedPolicyResult.Output | ConvertFrom-Json
            if ((ConvertTo-CanonicalJson -Value $observedOperatorPolicy) -ne
                (ConvertTo-CanonicalJson -Value $expectedOperatorPolicy)) {
                throw 'A política efetiva da role temporária diverge da lista de permissões aprovada.'
            }
            $operatorCredential = Get-OperatorCredential -RoleArn $operatorRoleArn
            $operatorIdentity = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
                'sts', 'get-caller-identity', '--output', 'json'
            )
            if ($operatorIdentity.Arn -notlike "arn:aws:sts::$accountId`:assumed-role/$OperatorRoleName/*") {
                throw 'A identidade efetiva não corresponde à role temporária esperada.'
            }
        }
    }
    elseif ($Phase -eq 'Cleanup' -and
        (Get-AwsErrorCode -Output $operatorRoleResult.Output) -eq 'NoSuchEntity' -and
        (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        $resumeState = Read-State
        $deleteIntent = Get-Mutation -State $resumeState -OperationId 'final-delete-operator-role'
        if (-not $deleteIntent -or $resumeState.Status -ne 'cleanup-finalizing' -or
            $resumeState.Cleanup.Status -ne 'finalizing' -or $resumeState.Created.Trail -or
            $resumeState.Created.TrailBucket -or $resumeState.Created.ObjectBucket -or
            @($resumeState.Created.RoleNames).Count -gt 0 -or
            @($resumeState.Created.AliasNames).Count -gt 0) {
            throw 'A role temporária está ausente antes da fronteira final autenticada do cleanup.'
        }
        $operatorRoleAbsentForCleanup = $true
    }
    else {
        throw "A role temporária do experimento não existe ou não pôde ser validada: $($operatorRoleResult.Output)"
    }
}

if ($Phase -eq 'Preflight') {
    $collisions = @()
    foreach ($roleName in $dataRoleNames) {
        $roleCheck = Invoke-ProfileAws -Arguments @(
            'iam', 'get-role', '--profile', $Profile, '--role-name', $roleName, '--output', 'json'
        )
        if ($roleCheck.ExitCode -eq 0) { $collisions += "role:$roleName" }
        elseif ((Get-AwsErrorCode -Output $roleCheck.Output) -ne 'NoSuchEntity') {
            throw 'O preflight não conseguiu provar a ausência de uma role planejada.'
        }
    }
    foreach ($bucket in @("$Prefix-ct", "$Prefix-obj")) {
        $bucketCheck = Invoke-ProfileAws -Arguments @(
            's3api', 'head-bucket', '--profile', $Profile, '--bucket', $bucket,
            '--expected-bucket-owner', $accountId
        )
        if ($bucketCheck.ExitCode -eq 0 -or
            (Get-AwsErrorCode -Output $bucketCheck.Output) -eq '403') {
            $collisions += "bucket:$bucket"
        }
        elseif ((Get-AwsErrorCode -Output $bucketCheck.Output) -notin @('404', 'NoSuchBucket')) {
            throw 'O preflight não conseguiu provar a disponibilidade de um bucket planejado.'
        }
    }
    $existingTrailsResult = Invoke-ProfileAws -Arguments @(
        'cloudtrail', 'list-trails', '--profile', $Profile, '--region', $Region, '--output', 'json'
    )
    if ($existingTrailsResult.ExitCode -ne 0) {
        throw "O preflight não conseguiu inventariar os trails: $($existingTrailsResult.Output)"
    }
    $existingTrails = $existingTrailsResult.Output | ConvertFrom-Json
    if ($existingTrails.Trails.Name -contains "$Prefix-trail") { $collisions += "trail:$Prefix-trail" }
    $existingAliasesResult = Invoke-ProfileAws -Arguments @(
        'kms', 'list-aliases', '--profile', $Profile, '--region', $Region, '--output', 'json'
    )
    if ($existingAliasesResult.ExitCode -ne 0) {
        throw "O preflight não conseguiu inventariar os aliases: $($existingAliasesResult.Output)"
    }
    $existingAliases = $existingAliasesResult.Output | ConvertFrom-Json
    foreach ($alias in @("alias/$Prefix-key-a", "alias/$Prefix-key-b")) {
        if ($existingAliases.Aliases.AliasName -contains $alias) { $collisions += "alias:$alias" }
    }
    if (Test-Path -LiteralPath $StateRoot) { $collisions += "state-root:$StateRoot" }
    if ($collisions.Count -gt 0) {
        throw "O preflight encontrou colisões: $($collisions -join ', ')"
    }

    [pscustomobject]@{
        Account = $accountId
        Profile = $Profile
        Region = $Region
        RunId = $RunId
        Prefix = $Prefix
        OperatorRole = $OperatorRoleName
        OperatorIdentity = $operatorIdentity.Arn
        CostCeilingUsd = 0.25
        PendingDeletionKeys = 2
        PendingDeletionDays = 7
        ComplianceResidueAccepted = $true
        Collisions = 0
    } | ConvertTo-Json -Compress
    exit 0
}

if ($Phase -eq 'Provision') {
    if (Test-Path -LiteralPath $StateRoot) {
        throw 'Já existe um diretório durável para este RunId. Use a fase apropriada para retomar ou limpar.'
    }
    $provisionCollisions = @()
    foreach ($roleName in $dataRoleNames) {
        $roleCheck = Invoke-ProfileAws -Arguments @(
            'iam', 'get-role', '--profile', $Profile, '--role-name', $roleName, '--output', 'json'
        )
        if ($roleCheck.ExitCode -eq 0) { $provisionCollisions += "role:$roleName" }
        elseif ((Get-AwsErrorCode -Output $roleCheck.Output) -ne 'NoSuchEntity') {
            throw 'Provision não conseguiu provar a ausência de uma role planejada.'
        }
    }
    foreach ($bucket in @("$Prefix-ct", "$Prefix-obj")) {
        $bucketCheck = Invoke-ProfileAws -Arguments @(
            's3api', 'head-bucket', '--profile', $Profile, '--bucket', $bucket,
            '--expected-bucket-owner', $accountId
        )
        if ($bucketCheck.ExitCode -eq 0 -or
            (Get-AwsErrorCode -Output $bucketCheck.Output) -eq '403') {
            $provisionCollisions += "bucket:$bucket"
        }
        elseif ((Get-AwsErrorCode -Output $bucketCheck.Output) -notin @('404', 'NoSuchBucket')) {
            throw 'Provision não conseguiu provar a disponibilidade de um bucket planejado.'
        }
    }
    $provisionTrailsResult = Invoke-ProfileAws -Arguments @(
        'cloudtrail', 'list-trails', '--profile', $Profile, '--region', $Region, '--output', 'json'
    )
    if ($provisionTrailsResult.ExitCode -ne 0) {
        throw "Provision não conseguiu inventariar os trails: $($provisionTrailsResult.Output)"
    }
    $provisionTrails = $provisionTrailsResult.Output | ConvertFrom-Json
    if ($provisionTrails.Trails.Name -contains "$Prefix-trail") { $provisionCollisions += "trail:$Prefix-trail" }
    $provisionAliasesResult = Invoke-ProfileAws -Arguments @(
        'kms', 'list-aliases', '--profile', $Profile, '--region', $Region, '--output', 'json'
    )
    if ($provisionAliasesResult.ExitCode -ne 0) {
        throw "Provision não conseguiu inventariar os aliases: $($provisionAliasesResult.Output)"
    }
    $provisionAliases = $provisionAliasesResult.Output | ConvertFrom-Json
    foreach ($alias in @("alias/$Prefix-key-a", "alias/$Prefix-key-b")) {
        if ($provisionAliases.Aliases.AliasName -contains $alias) { $provisionCollisions += "alias:$alias" }
    }
    if ($provisionCollisions.Count -gt 0) {
        throw "Provision recusado por colisões: $($provisionCollisions -join ', ')"
    }

    Initialize-StateStorage

$state = [ordered]@{
    SchemaVersion = 6
    Status = 'provisioning'
    AccountId = $accountId
    RunId = $RunId
    Prefix = $Prefix
    Region = $Region
    ProvisionStartedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    ExpiresAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow.AddHours(4))
    ExerciseCompletedAt = $null
    Authorization = [ordered]@{
        CostCeilingUsd = 0.25
        PendingDeletionKeys = 2
        PendingDeletionDays = 7
        ComplianceResidueAccepted = $true
    }
    OperatorRoleName = $OperatorRoleName
    OperatorRoleArn = $operatorRoleArn
    TrailBucket = "$Prefix-ct"
    ObjectBucket = "$Prefix-obj"
    TrailName = "$Prefix-trail"
    TrailArn = "arn:aws:cloudtrail:$Region`:$accountId`:trail/$Prefix-trail"
    DataRoleNames = $dataRoleNames
    Roles = $roles
    KeyAAlias = "alias/$Prefix-key-a"
    KeyBAlias = "alias/$Prefix-key-b"
    KeyAArn = $null
    KeyAId = $null
    KeyBArn = $null
    KeyBId = $null
    ReadyAfter = $null
    Created = [ordered]@{
        OperatorRole = $true
        TrailBucket = $false
        Trail = $false
        TrailLoggingStarted = $false
        ObjectBucket = $false
        RoleNames = @()
        KeyIds = @()
        AliasNames = @()
    }
    Tests = @()
    Mutations = @()
    ExpectedEvents = @()
    Evidence = [ordered]@{
        Collected = $false
        LeakScanPassed = $false
        CloudTrailEvents = 0
    }
    Cleanup = [ordered]@{
        Status = 'not-started'
        ProvisionReconciled = $false
        IndeterminateProvisionOperations = @()
        Residues = @()
        ExpectedTrailEvents = @()
        ExpectedFinalEvents = @()
        LastAuditedMutationAt = $null
    }
    UpdatedAt = $null
}
Save-State -State $state

try {
    $bucketTags = [ordered]@{
        TagSet = @(
            [ordered]@{ Key = 'RunId'; Value = $state.RunId },
            [ordered]@{ Key = 'AraiaTask'; Value = 'Task6' },
            [ordered]@{ Key = 'ManagedBy'; Value = 'Araia' }
        )
    } | ConvertTo-Json -Depth 6 -Compress

    $publicAccessBlock = [ordered]@{
        BlockPublicAcls = $true
        IgnorePublicAcls = $true
        BlockPublicPolicy = $true
        RestrictPublicBuckets = $true
    } | ConvertTo-Json -Compress

    Invoke-TrackedProfileMutation -State $state -OperationId 'create-trail-bucket' `
        -EventSource 's3.amazonaws.com' -EventName 'CreateBucket' `
        -ResourceTokens @($state.TrailBucket) -Arguments @(
            's3api', 'create-bucket', '--profile', $Profile, '--bucket', $state.TrailBucket,
            '--object-ownership', 'BucketOwnerEnforced', '--region', $Region
        ) | Out-Null
    $state.Created.TrailBucket = $true
    Save-State -State $state
    Write-Journal -Type 'create' -ResourceType 's3-bucket' -ResourceId $state.TrailBucket -Status 'succeeded'
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'configure-trail-bucket-public-access' `
        -EventSource 's3.amazonaws.com' -EventName 'PutBucketPublicAccessBlock' `
        -ResourceTokens @($state.TrailBucket) -Arguments @(
        's3api', 'put-public-access-block', '--profile', $Profile, '--bucket', $state.TrailBucket,
        '--public-access-block-configuration', $publicAccessBlock
    ) | Out-Null
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'configure-trail-bucket-encryption' `
        -EventSource 's3.amazonaws.com' -EventName 'PutBucketEncryption' `
        -ResourceTokens @($state.TrailBucket) -Arguments @(
        's3api', 'put-bucket-encryption', '--profile', $Profile, '--bucket', $state.TrailBucket,
        '--server-side-encryption-configuration',
        '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"},"BucketKeyEnabled":false}]}'
    ) | Out-Null
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'tag-trail-bucket' -EventSource 's3.amazonaws.com' `
        -EventName 'PutBucketTagging' -ResourceTokens @($state.TrailBucket, $RunId) `
        -Arguments @(
        's3api', 'put-bucket-tagging', '--profile', $Profile, '--bucket', $state.TrailBucket,
        '--tagging', $bucketTags
    ) | Out-Null
    $trailBucketTags = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        's3api', 'get-bucket-tagging', '--bucket', $state.TrailBucket, '--output', 'json'
    )
    if (-not (Test-ExpectedTags -Tags $trailBucketTags.TagSet -KeyName 'Key' -ValueName 'Value')) {
        throw 'O bucket de auditoria não confirmou as tags de ownership.'
    }
    $trailOwnership = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        's3api', 'get-bucket-ownership-controls', '--bucket', $state.TrailBucket, '--output', 'json'
    )
    if (@($trailOwnership.OwnershipControls.Rules).Count -ne 1 -or
        $trailOwnership.OwnershipControls.Rules[0].ObjectOwnership -ne 'BucketOwnerEnforced') {
        throw 'O bucket de auditoria não confirmou BucketOwnerEnforced.'
    }

    $trailBucketPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Sid = 'AWSCloudTrailAclCheck'
                Effect = 'Allow'
                Principal = [ordered]@{ Service = 'cloudtrail.amazonaws.com' }
                Action = 's3:GetBucketAcl'
                Resource = "arn:aws:s3:::$($state.TrailBucket)"
                Condition = [ordered]@{
                    StringEquals = [ordered]@{ 'aws:SourceArn' = $state.TrailArn }
                }
            },
            [ordered]@{
                Sid = 'AWSCloudTrailWrite'
                Effect = 'Allow'
                Principal = [ordered]@{ Service = 'cloudtrail.amazonaws.com' }
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.TrailBucket)/AWSLogs/$accountId/*"
                Condition = [ordered]@{
                    StringEquals = [ordered]@{
                        's3:x-amz-acl' = 'bucket-owner-full-control'
                        'aws:SourceArn' = $state.TrailArn
                    }
                }
            },
            [ordered]@{
                Sid = 'DenyInsecureTransport'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:*'
                Resource = @(
                    "arn:aws:s3:::$($state.TrailBucket)",
                    "arn:aws:s3:::$($state.TrailBucket)/*"
                )
                Condition = [ordered]@{ Bool = [ordered]@{ 'aws:SecureTransport' = 'false' } }
            }
        )
    } | ConvertTo-Json -Depth 12 -Compress
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'configure-trail-bucket-policy' `
        -EventSource 's3.amazonaws.com' -EventName 'PutBucketPolicy' `
        -ResourceTokens @($state.TrailBucket) -Arguments @(
        's3api', 'put-bucket-policy', '--profile', $Profile, '--bucket', $state.TrailBucket,
        '--policy', $trailBucketPolicy
    ) | Out-Null

    Invoke-TrackedProfileMutation -State $state -OperationId 'create-trail' `
        -EventSource 'cloudtrail.amazonaws.com' -EventName 'CreateTrail' `
        -ResourceTokens @($state.TrailName) -Arguments @(
            'cloudtrail', 'create-trail', '--profile', $Profile, '--name', $state.TrailName,
            '--s3-bucket-name', $state.TrailBucket,
            '--enable-log-file-validation', '--no-is-multi-region-trail',
            '--include-global-service-events',
            '--tags-list', "Key=RunId,Value=$RunId", 'Key=AraiaTask,Value=Task6',
            '--region', $Region
        ) | Out-Null
    $state.Created.Trail = $true
    Save-State -State $state
    Write-Journal -Type 'create' -ResourceType 'cloudtrail-trail' -ResourceId $state.TrailArn -Status 'succeeded'
    $createdTrailTags = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        'cloudtrail', 'list-tags', '--resource-id-list', $state.TrailArn,
        '--region', $Region, '--output', 'json'
    )
    if (-not (Test-ExpectedTags -Tags @($createdTrailTags.ResourceTagList[0].TagsList) `
        -KeyName 'Key' -ValueName 'Value')) {
        throw 'O trail não confirmou as tags de ownership.'
    }
    $advancedSelectors = @(
        [ordered]@{
            Name = 'Eventos de gerenciamento'
            FieldSelectors = @(
                [ordered]@{ Field = 'eventCategory'; Equals = @('Management') }
            )
        },
        [ordered]@{
            Name = 'Eventos de dados de objetos do S3'
            FieldSelectors = @(
                [ordered]@{ Field = 'eventCategory'; Equals = @('Data') },
                [ordered]@{ Field = 'resources.type'; Equals = @('AWS::S3::Object') },
                [ordered]@{
                    Field = 'resources.ARN'
                    StartsWith = @("arn:aws:s3:::$($state.ObjectBucket)/")
                }
            )
        }
    ) | ConvertTo-Json -Depth 10 -Compress
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'configure-trail-selectors' `
        -EventSource 'cloudtrail.amazonaws.com' -EventName 'PutEventSelectors' `
        -ResourceTokens @($state.TrailName, $state.ObjectBucket) -Arguments @(
        'cloudtrail', 'put-event-selectors', '--profile', $Profile, '--trail-name', $state.TrailName,
        '--advanced-event-selectors', $advancedSelectors, '--region', $Region
    ) | Out-Null
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'start-trail-logging' `
        -EventSource 'cloudtrail.amazonaws.com' -EventName 'StartLogging' `
        -ResourceTokens @($state.TrailName) -Arguments @(
        'cloudtrail', 'start-logging', '--profile', $Profile,
        '--name', $state.TrailName, '--region', $Region
    ) | Out-Null
    $state.Created.TrailLoggingStarted = $true
    Save-State -State $state

    $trailStatus = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        'cloudtrail', 'get-trail-status', '--name', $state.TrailName,
        '--region', $Region, '--output', 'json'
    )
    if (-not [string]::IsNullOrWhiteSpace($trailStatus.LatestDeliveryError) -or
        -not [string]::IsNullOrWhiteSpace($trailStatus.LatestDigestDeliveryError)) {
        throw 'O CloudTrail reportou erro de entrega de log ou digest.'
    }
    if (-not $trailStatus.IsLogging) {
        throw 'CloudTrail não confirmou IsLogging=true.'
    }
    $trailConfiguration = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        'cloudtrail', 'get-trail', '--name', $state.TrailName,
        '--region', $Region, '--output', 'json'
    )
    if ($trailConfiguration.Trail.HomeRegion -ne $Region -or
        -not $trailConfiguration.Trail.IncludeGlobalServiceEvents -or
        $trailConfiguration.Trail.IsMultiRegionTrail -or
        -not $trailConfiguration.Trail.LogFileValidationEnabled -or
        $trailConfiguration.Trail.S3BucketName -ne $state.TrailBucket) {
        throw 'A configuração integral do trail divergiu do envelope aprovado.'
    }
    $observedSelectors = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        'cloudtrail', 'get-event-selectors', '--trail-name', $state.TrailName,
        '--region', $Region, '--output', 'json'
    )
    $expectedSelectors = $advancedSelectors | ConvertFrom-Json
    if ((ConvertTo-CanonicalJson -Value $observedSelectors.AdvancedEventSelectors) -ne
        (ConvertTo-CanonicalJson -Value $expectedSelectors)) {
        throw 'Os advanced event selectors do CloudTrail divergiram do envelope aprovado.'
    }

    Invoke-TrackedProfileMutation -State $state -OperationId 'create-object-bucket' `
        -EventSource 's3.amazonaws.com' -EventName 'CreateBucket' `
        -ResourceTokens @($state.ObjectBucket) -Arguments @(
            's3api', 'create-bucket', '--profile', $Profile, '--bucket', $state.ObjectBucket,
            '--object-lock-enabled-for-bucket', '--object-ownership', 'BucketOwnerEnforced',
            '--region', $Region
        ) | Out-Null
    $state.Created.ObjectBucket = $true
    $state.ReadyAfter = ConvertTo-CanonicalUtcTimestamp -Value (
        [System.DateTimeOffset]::UtcNow.AddMinutes(15)
    )
    Save-State -State $state
    Write-Journal -Type 'create' -ResourceType 's3-bucket' -ResourceId $state.ObjectBucket -Status 'succeeded'
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'enable-object-bucket-versioning' `
        -EventSource 's3.amazonaws.com' -EventName 'PutBucketVersioning' `
        -ResourceTokens @($state.ObjectBucket) -Arguments @(
        's3api', 'put-bucket-versioning', '--profile', $Profile, '--bucket', $state.ObjectBucket,
        '--versioning-configuration', 'Status=Enabled'
    ) | Out-Null
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'configure-object-bucket-public-access' `
        -EventSource 's3.amazonaws.com' -EventName 'PutBucketPublicAccessBlock' `
        -ResourceTokens @($state.ObjectBucket) -Arguments @(
        's3api', 'put-public-access-block', '--profile', $Profile, '--bucket', $state.ObjectBucket,
        '--public-access-block-configuration', $publicAccessBlock
    ) | Out-Null
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'tag-object-bucket' -EventSource 's3.amazonaws.com' `
        -EventName 'PutBucketTagging' -ResourceTokens @($state.ObjectBucket, $RunId) `
        -Arguments @(
        's3api', 'put-bucket-tagging', '--profile', $Profile, '--bucket', $state.ObjectBucket,
        '--tagging', $bucketTags
    ) | Out-Null
    $objectBucketTags = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        's3api', 'get-bucket-tagging', '--bucket', $state.ObjectBucket, '--output', 'json'
    )
    if (-not (Test-ExpectedTags -Tags $objectBucketTags.TagSet -KeyName 'Key' -ValueName 'Value')) {
        throw 'O bucket de objetos não confirmou as tags de ownership.'
    }
    $objectOwnership = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        's3api', 'get-bucket-ownership-controls', '--bucket', $state.ObjectBucket, '--output', 'json'
    )
    if (@($objectOwnership.OwnershipControls.Rules).Count -ne 1 -or
        $objectOwnership.OwnershipControls.Rules[0].ObjectOwnership -ne 'BucketOwnerEnforced') {
        throw 'O bucket de objetos não confirmou BucketOwnerEnforced.'
    }

    $dataRoleTrust = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $operatorRoleArn }
                Action = 'sts:AssumeRole'
            }
        )
    } | ConvertTo-Json -Depth 8 -Compress
    $roleTags = @(
        [ordered]@{ Key = 'RunId'; Value = $state.RunId },
        [ordered]@{ Key = 'AraiaTask'; Value = 'Task6' },
        [ordered]@{ Key = 'ManagedBy'; Value = 'Araia' }
    ) | ConvertTo-Json -Depth 4 -Compress

    foreach ($roleName in $dataRoleNames) {
        Invoke-TrackedProfileMutation -State $state -OperationId "create-data-role:$roleName" `
            -EventSource 'iam.amazonaws.com' -EventName 'CreateRole' `
            -ResourceTokens @($roleName) -Arguments @(
                'iam', 'create-role', '--profile', $Profile, '--role-name', $roleName,
                '--assume-role-policy-document', $dataRoleTrust,
                '--max-session-duration', '3600', '--tags', $roleTags
            ) | Out-Null
        $state.Created.RoleNames = @($state.Created.RoleNames) + $roleName
        Save-State -State $state
        Write-Journal -Type 'create' -ResourceType 'iam-role' -ResourceId $roleName -Status 'succeeded'
        $createdRole = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
            'iam', 'get-role', '--role-name', $roleName, '--output', 'json'
        )
        if (-not (Test-ExpectedTags -Tags $createdRole.Role.Tags -KeyName 'Key' -ValueName 'Value') -or
            (ConvertTo-CanonicalJson -Value $createdRole.Role.AssumeRolePolicyDocument) -ne
                (ConvertTo-CanonicalJson -Value ($dataRoleTrust | ConvertFrom-Json)) -or
            $createdRole.Role.MaxSessionDuration -ne 3600 -or $createdRole.Role.Path -ne '/') {
            throw "A role $roleName não confirmou as tags de ownership."
        }
    }

    $applicationACondition = [ordered]@{
        StringEquals = [ordered]@{
            'kms:ViaService' = "s3.$Region.amazonaws.com"
            'kms:CallerAccount' = $accountId
            'kms:EncryptionContext:application' = 'app-a'
        }
        ArnLike = [ordered]@{
            'kms:EncryptionContext:aws:s3:arn' = "arn:aws:s3:::$($state.ObjectBucket)/app-a/*"
        }
    }
    $applicationBCondition = [ordered]@{
        StringEquals = [ordered]@{
            'kms:ViaService' = "s3.$Region.amazonaws.com"
            'kms:CallerAccount' = $accountId
            'kms:EncryptionContext:application' = 'app-b'
        }
        ArnLike = [ordered]@{
            'kms:EncryptionContext:aws:s3:arn' = "arn:aws:s3:::$($state.ObjectBucket)/app-b/*"
        }
    }

    $keyAPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Sid = 'AdministratorAdministration'
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $adminRole.Role.Arn }
                Action = 'kms:*'
                Resource = '*'
            },
            [ordered]@{
                Sid = 'OperatorAdministration'
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $operatorRoleArn }
                Action = @(
                    'kms:DescribeKey', 'kms:ListResourceTags',
                    'kms:EnableKey', 'kms:DisableKey',
                    'kms:RotateKeyOnDemand', 'kms:ListKeyRotations',
                    'kms:ScheduleKeyDeletion', 'kms:DeleteAlias'
                )
                Resource = '*'
            },
            [ordered]@{
                Sid = 'UploadAUse'
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $roles.UploadA.Arn }
                Action = 'kms:GenerateDataKey'
                Resource = '*'
                Condition = $applicationACondition
            },
            [ordered]@{
                Sid = 'ReadAUse'
                Effect = 'Allow'
                Principal = [ordered]@{
                    AWS = @($roles.ValidatorA.Arn, $roles.CurrentVersionProbe.Arn)
                }
                Action = 'kms:Decrypt'
                Resource = '*'
                Condition = $applicationACondition
            }
        )
    } | ConvertTo-Json -Depth 14 -Compress
    $keyBPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Sid = 'AdministratorAdministration'
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $adminRole.Role.Arn }
                Action = 'kms:*'
                Resource = '*'
            },
            [ordered]@{
                Sid = 'OperatorAdministration'
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $operatorRoleArn }
                Action = @(
                    'kms:DescribeKey', 'kms:ListResourceTags',
                    'kms:EnableKey', 'kms:DisableKey',
                    'kms:RotateKeyOnDemand', 'kms:ListKeyRotations',
                    'kms:ScheduleKeyDeletion', 'kms:DeleteAlias'
                )
                Resource = '*'
            },
            [ordered]@{
                Sid = 'UploadBUse'
                Effect = 'Allow'
                Principal = [ordered]@{ AWS = $roles.UploadB.Arn }
                Action = 'kms:GenerateDataKey'
                Resource = '*'
                Condition = $applicationBCondition
            }
        )
    } | ConvertTo-Json -Depth 14 -Compress
    $keyTags = @(
        [ordered]@{ TagKey = 'RunId'; TagValue = $state.RunId },
        [ordered]@{ TagKey = 'AraiaTask'; TagValue = 'Task6' },
        [ordered]@{ TagKey = 'ManagedBy'; TagValue = 'Araia' }
    ) | ConvertTo-Json -Depth 4 -Compress

    function New-ExperimentKey {
        param(
            [Parameter(Mandatory = $true)][string] $OperationId,
            [Parameter(Mandatory = $true)][string] $Description,
            [Parameter(Mandatory = $true)][string] $Policy
        )

        $intent = Start-MutationIntent -State $state -OperationId $OperationId `
            -ActorRoleName $adminRoleName -EventSource 'kms.amazonaws.com' `
            -EventName 'CreateKey' -ResourceTokens @($Description, $RunId)
        if ($intent.Status -in @('succeeded', 'reconciled')) {
            throw "A criação $OperationId já foi concluída e não pode ser repetida."
        }
        for ($attemptNumber = 1; $attemptNumber -le 6; $attemptNumber++) {
            $attemptRecord = Start-MutationAttempt -State $state -OperationId $OperationId
            $result = Invoke-ProfileAwsSingleAttempt -Arguments @(
                'kms', 'create-key', '--profile', $Profile, '--description', $Description,
                '--key-usage', 'ENCRYPT_DECRYPT', '--key-spec', 'SYMMETRIC_DEFAULT',
                '--origin', 'AWS_KMS', '--policy', $Policy, '--tags', $keyTags,
                '--region', $Region, '--output', 'json'
            )
            $errorCode = Get-AwsErrorCode -Output $result.Output
            if ($result.ExitCode -eq 0) {
                Complete-MutationAttempt -State $state -OperationId $OperationId `
                    -AttemptId $attemptRecord.AttemptId -LocalOutcome 'succeeded' `
                    -ExitCode $result.ExitCode -ErrorCode $null
                Complete-Mutation -State $state -OperationId $OperationId
                return $result.Output | ConvertFrom-Json
            }
            if ($result.Output -notmatch 'MalformedPolicyDocument|invalid principals') {
                $failureDisposition = Get-FailureDisposition -Output $result.Output `
                    -ExitCode $result.ExitCode
                Complete-MutationAttempt -State $state -OperationId $OperationId `
                    -AttemptId $attemptRecord.AttemptId `
                    -LocalOutcome $failureDisposition `
                    -ExitCode $result.ExitCode -ErrorCode $errorCode
                if (Test-AllMutationAttemptsDefinitelyFailed -State $state `
                    -OperationId $OperationId) {
                    Complete-Mutation -State $state -OperationId $OperationId `
                        -Status 'not-applied'
                }
                throw "Falha ao criar CMK: $($result.Output)"
            }
            Complete-MutationAttempt -State $state -OperationId $OperationId `
                -AttemptId $attemptRecord.AttemptId -LocalOutcome 'failed-definitive' `
                -ExitCode $result.ExitCode -ErrorCode $errorCode
            if ($attemptNumber -lt 6) { Start-Sleep -Seconds 5 }
        }

        Complete-Mutation -State $state -OperationId $OperationId -Status 'not-applied'
        throw 'A propagação das roles não permitiu criar a CMK.'
    }

    $keyA = New-ExperimentKey -OperationId 'create-key-a' `
        -Description "$Prefix chave A" -Policy $keyAPolicy
    $state.KeyAArn = $keyA.KeyMetadata.Arn
    $state.KeyAId = $keyA.KeyMetadata.KeyId
    $state.Created.KeyIds = @($state.Created.KeyIds) + $state.KeyAId
    Save-State -State $state
    Write-Journal -Type 'create' -ResourceType 'kms-key' -ResourceId $state.KeyAArn -Status 'succeeded'
    $keyATags = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        'kms', 'list-resource-tags', '--key-id', $state.KeyAArn, '--region', $Region, '--output', 'json'
    )
    if (-not (Test-ExpectedTags -Tags $keyATags.Tags -KeyName 'TagKey' -ValueName 'TagValue')) {
        throw 'A CMK A não confirmou as tags de ownership.'
    }

    $keyB = New-ExperimentKey -OperationId 'create-key-b' `
        -Description "$Prefix chave B" -Policy $keyBPolicy
    $state.KeyBArn = $keyB.KeyMetadata.Arn
    $state.KeyBId = $keyB.KeyMetadata.KeyId
    $state.Created.KeyIds = @($state.Created.KeyIds) + $state.KeyBId
    Save-State -State $state
    Write-Journal -Type 'create' -ResourceType 'kms-key' -ResourceId $state.KeyBArn -Status 'succeeded'
    $keyBTags = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        'kms', 'list-resource-tags', '--key-id', $state.KeyBArn, '--region', $Region, '--output', 'json'
    )
    if (-not (Test-ExpectedTags -Tags $keyBTags.Tags -KeyName 'TagKey' -ValueName 'TagValue')) {
        throw 'A CMK B não confirmou as tags de ownership.'
    }

    Invoke-TrackedProfileMutation -State $state -OperationId 'create-alias-a' `
        -EventSource 'kms.amazonaws.com' -EventName 'CreateAlias' `
        -ResourceTokens @($state.KeyAAlias, $state.KeyAId) -Arguments @(
        'kms', 'create-alias', '--profile', $Profile, '--alias-name', $state.KeyAAlias,
        '--target-key-id', $state.KeyAId, '--region', $Region
    ) | Out-Null
    $state.Created.AliasNames = @($state.Created.AliasNames) + $state.KeyAAlias
    Save-State -State $state
    Write-Journal -Type 'create' -ResourceType 'kms-alias' -ResourceId $state.KeyAAlias -Status 'succeeded'
    Invoke-TrackedProfileMutation -State $state -OperationId 'create-alias-b' `
        -EventSource 'kms.amazonaws.com' -EventName 'CreateAlias' `
        -ResourceTokens @($state.KeyBAlias, $state.KeyBId) -Arguments @(
        'kms', 'create-alias', '--profile', $Profile, '--alias-name', $state.KeyBAlias,
        '--target-key-id', $state.KeyBId, '--region', $Region
    ) | Out-Null
    $state.Created.AliasNames = @($state.Created.AliasNames) + $state.KeyBAlias
    Save-State -State $state
    Write-Journal -Type 'create' -ResourceType 'kms-alias' -ResourceId $state.KeyBAlias -Status 'succeeded'

    $bucketEncryption = [ordered]@{
        Rules = @(
            [ordered]@{
                ApplyServerSideEncryptionByDefault = [ordered]@{
                    SSEAlgorithm = 'aws:kms'
                    KMSMasterKeyID = $state.KeyAArn
                }
                BucketKeyEnabled = $false
            }
        )
    } | ConvertTo-Json -Depth 8 -Compress
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'configure-object-bucket-encryption' `
        -EventSource 's3.amazonaws.com' -EventName 'PutBucketEncryption' `
        -ResourceTokens @($state.ObjectBucket, $state.KeyAArn) -Arguments @(
        's3api', 'put-bucket-encryption', '--profile', $Profile, '--bucket', $state.ObjectBucket,
        '--server-side-encryption-configuration', $bucketEncryption
    ) | Out-Null

    $objectBucketPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Sid = 'DenyInsecureTransport'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:*'
                Resource = @(
                    "arn:aws:s3:::$($state.ObjectBucket)",
                    "arn:aws:s3:::$($state.ObjectBucket)/*"
                )
                Condition = [ordered]@{ Bool = [ordered]@{ 'aws:SecureTransport' = 'false' } }
            },
            [ordered]@{
                Sid = 'DenyMissingKmsEncryption'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/*"
                Condition = [ordered]@{
                    StringNotEquals = [ordered]@{
                        's3:x-amz-server-side-encryption' = 'aws:kms'
                    }
                }
            },
            [ordered]@{
                Sid = 'DenyWrongKeyForApplicationA'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-a/*"
                Condition = [ordered]@{
                    StringNotEquals = [ordered]@{
                        's3:x-amz-server-side-encryption-aws-kms-key-id' = $state.KeyAArn
                    }
                }
            },
            [ordered]@{
                Sid = 'DenyWrongKeyForApplicationB'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-b/*"
                Condition = [ordered]@{
                    StringNotEquals = [ordered]@{
                        's3:x-amz-server-side-encryption-aws-kms-key-id' = $state.KeyBArn
                    }
                }
            },
            [ordered]@{
                Sid = 'DenyApplicationAWritesFromOtherRoles'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-a/*"
                Condition = [ordered]@{
                    ArnNotEquals = [ordered]@{ 'aws:PrincipalArn' = $roles.UploadA.Arn }
                }
            },
            [ordered]@{
                Sid = 'DenyApplicationBWritesFromOtherRoles'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-b/*"
                Condition = [ordered]@{
                    ArnNotEquals = [ordered]@{ 'aws:PrincipalArn' = $roles.UploadB.Arn }
                }
            },
            [ordered]@{
                Sid = 'DenyVersionDeletionFromOtherRoles'
                Effect = 'Deny'
                Principal = '*'
                Action = @('s3:DeleteObject', 's3:DeleteObjectVersion')
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/*"
                Condition = [ordered]@{
                    ArnNotEquals = [ordered]@{
                        'aws:PrincipalArn' = @($operatorRoleArn, $roles.DisposerA.Arn)
                    }
                }
            },
            [ordered]@{
                Sid = 'DenyRetentionBeyondOperationalCeiling'
                Effect = 'Deny'
                Principal = '*'
                Action = 's3:PutObjectRetention'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/*"
                Condition = [ordered]@{
                    NumericGreaterThan = [ordered]@{
                        's3:object-lock-remaining-retention-days' = '1'
                    }
                }
            }
        )
    } | ConvertTo-Json -Depth 14 -Compress
    Invoke-TrackedProfileMutation -State $state `
        -OperationId 'configure-object-bucket-policy' `
        -EventSource 's3.amazonaws.com' -EventName 'PutBucketPolicy' `
        -ResourceTokens @($state.ObjectBucket) -Arguments @(
        's3api', 'put-bucket-policy', '--profile', $Profile, '--bucket', $state.ObjectBucket,
        '--policy', $objectBucketPolicy
    ) | Out-Null

    function Put-DataRolePolicy {
        param(
            [Parameter(Mandatory = $true)][string] $RoleName,
            [Parameter(Mandatory = $true)][hashtable] $Policy
        )

        $json = $Policy | ConvertTo-Json -Depth 14 -Compress
        Invoke-TrackedProfileMutation -State $state `
            -OperationId "put-data-role-policy:$RoleName" `
            -EventSource 'iam.amazonaws.com' -EventName 'PutRolePolicy' `
            -ResourceTokens @($RoleName, 'ExperimentDataPolicy') -Arguments @(
            'iam', 'put-role-policy', '--profile', $Profile, '--role-name', $RoleName,
            '--policy-name', 'ExperimentDataPolicy', '--policy-document', $json
        ) | Out-Null
    }

    $uploadAPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect = 'Allow'
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-a/*"
            },
            [ordered]@{
                Effect = 'Allow'
                Action = 'kms:GenerateDataKey'
                Resource = $state.KeyAArn
                Condition = $applicationACondition
            }
        )
    }
    Put-DataRolePolicy -RoleName $roles.UploadA.Name -Policy $uploadAPolicy

    $uploadBPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect = 'Allow'
                Action = 's3:PutObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-b/*"
            },
            [ordered]@{
                Effect = 'Allow'
                Action = 'kms:GenerateDataKey'
                Resource = $state.KeyBArn
                Condition = $applicationBCondition
            }
        )
    }
    Put-DataRolePolicy -RoleName $roles.UploadB.Name -Policy $uploadBPolicy

    $validatorPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect = 'Allow'
                Action = 's3:GetObjectVersion'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-a/*"
            },
            [ordered]@{
                Effect = 'Allow'
                Action = 'kms:Decrypt'
                Resource = $state.KeyAArn
                Condition = $applicationACondition
            }
        )
    }
    Put-DataRolePolicy -RoleName $roles.ValidatorA.Name -Policy $validatorPolicy

    $disposerPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect = 'Allow'
                Action = 's3:DeleteObjectVersion'
                Resource = @(
                    "arn:aws:s3:::$($state.ObjectBucket)/app-a/disposable/*",
                    "arn:aws:s3:::$($state.ObjectBucket)/app-a/locks/*"
                )
            },
            [ordered]@{
                Effect = 'Allow'
                Action = 's3:GetObjectRetention'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-a/locks/*"
            }
        )
    }
    Put-DataRolePolicy -RoleName $roles.DisposerA.Name -Policy $disposerPolicy

    $currentVersionPolicy = [ordered]@{
        Version = '2012-10-17'
        Statement = @(
            [ordered]@{
                Effect = 'Allow'
                Action = 's3:GetObject'
                Resource = "arn:aws:s3:::$($state.ObjectBucket)/app-a/identity/*"
            },
            [ordered]@{
                Effect = 'Allow'
                Action = 'kms:Decrypt'
                Resource = $state.KeyAArn
                Condition = $applicationACondition
            }
        )
    }
    Put-DataRolePolicy -RoleName $roles.CurrentVersionProbe.Name -Policy $currentVersionPolicy

    $versioning = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        's3api', 'get-bucket-versioning', '--bucket', $state.ObjectBucket, '--output', 'json'
    )
    if ($versioning.Status -ne 'Enabled') {
        throw 'O bucket de objetos não confirmou Versioning Enabled.'
    }
    $objectLock = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        's3api', 'get-object-lock-configuration', '--bucket', $state.ObjectBucket, '--output', 'json'
    )
    if ($objectLock.ObjectLockConfiguration.ObjectLockEnabled -ne 'Enabled') {
        throw 'O bucket de objetos não confirmou Object Lock Enabled.'
    }
    $encryption = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        's3api', 'get-bucket-encryption', '--bucket', $state.ObjectBucket, '--output', 'json'
    )
    $encryptionRule = $encryption.ServerSideEncryptionConfiguration.Rules[0]
    if ($encryptionRule.ApplyServerSideEncryptionByDefault.KMSMasterKeyID -ne $state.KeyAArn -or
        $encryptionRule.BucketKeyEnabled -ne $false) {
        throw 'A configuração SSE-KMS ou BucketKeyEnabled divergiu do envelope aprovado.'
    }
    foreach ($roleEntry in $roles.GetEnumerator()) {
        $credential = Get-DataCredential -OperatorCredential $operatorCredential `
            -RoleArn $roleEntry.Value.Arn -SessionName "preflight-$($roleEntry.Key.ToLowerInvariant())"
        $identity = Invoke-AwsJson -Credential $credential -Arguments @(
            'sts', 'get-caller-identity', '--output', 'json'
        )
        if ($identity.Arn -notlike "arn:aws:sts::$accountId`:assumed-role/$($roleEntry.Value.Name)/*") {
            throw "A role $($roleEntry.Value.Name) não produziu a identidade esperada."
        }
    }

    $state.Status = 'provisioned-not-verified'
    Save-State -State $state

    [pscustomobject]@{
        Status = $state.Status
        Prefix = $state.Prefix
        TrailLogging = $trailStatus.IsLogging
        ObjectLockEnabled = $true
        DataRoles = $state.DataRoleNames.Count
        Keys = 2
        BucketKeyEnabled = $false
    } | ConvertTo-Json -Compress
}
catch {
    $failure = $_.Exception.Message
    $state.Status = 'provision-failed'
    $state.ProvisionFailure = $failure
    Save-State -State $state
    throw "Provisionamento falhou. Nenhum rollback automático foi executado; use Cleanup, que valida ownership por recurso: $failure"
}
}

function Write-CommonEvent {
    param(
        [Parameter(Mandatory = $true)][string] $Item,
        [Parameter(Mandatory = $true)][string] $Actor,
        [Parameter(Mandatory = $true)][string] $Action,
        [Parameter(Mandatory = $true)][string] $Verdict,
        [Parameter(Mandatory = $true)][int] $ExitCode,
        [AllowNull()][string] $ErrorCode
    )

    $line = '{0}|item={1}|actor={2}|action={3}|verdict={4}|exit={5}|error={6}' -f `
        (ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)),
        $Item, $Actor, $Action, $Verdict, $ExitCode, $ErrorCode
    Add-Content -LiteralPath $CommonLogPath -Value $line -Encoding utf8
}

function Write-RestrictedEvent {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Entry)

    Add-Content -LiteralPath $RestrictedEvidencePath `
        -Value ($Entry | ConvertTo-Json -Depth 12 -Compress) -Encoding utf8
}

function Get-CloudTrailEventName {
    param([Parameter(Mandatory = $true)][string] $Operation)

    (($Operation -split '-') | ForEach-Object {
        [System.Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($_)
    }) -join ''
}

function Get-ArgumentValue {
    param(
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $index = [Array]::IndexOf($Arguments, $Name)
    if ($index -ge 0 -and $index + 1 -lt $Arguments.Count) {
        return $Arguments[$index + 1]
    }
    $null
}

function Add-ExpectedCloudTrailEvent {
    param(
        [Parameter(Mandatory = $true)][string] $CallId,
        [Parameter(Mandatory = $true)][string] $Item,
        [Parameter(Mandatory = $true)][string] $Actor,
        [Parameter(Mandatory = $true)][string] $Action,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][bool] $ExpectSuccess,
        [Parameter(Mandatory = $true)][string[]] $AcceptedErrorCodes,
        [AllowNull()][string] $ObservedErrorCode,
        [Parameter(Mandatory = $true)][string] $StartedAt,
        [AllowNull()][string] $CompletedAt,
        [AllowNull()][Nullable[int]] $ExitCode,
        [AllowNull()][string] $AttemptId
    )

    if (-not $script:ActiveState) {
        throw 'O estado ativo não foi definido para a correlação CloudTrail.'
    }
    $service = $Arguments[0]
    $eventSource = switch ($service) {
        's3api' { 's3.amazonaws.com' }
        'kms' { 'kms.amazonaws.com' }
        'iam' { 'iam.amazonaws.com' }
        'cloudtrail' { 'cloudtrail.amazonaws.com' }
        'sts' { 'sts.amazonaws.com' }
        default { throw "Serviço AWS não mapeado para CloudTrail: $service" }
    }
    $actorRoleName = if ($Actor -eq 'Operator') {
        $script:ActiveState.OperatorRoleName
    }
    elseif ($script:ActiveState.Roles.Contains($Actor)) {
        $script:ActiveState.Roles[$Actor].Name
    }
    elseif ($Actor -eq 'Administrator') {
        $adminRoleName
    }
    else {
        throw "Ator não mapeado para CloudTrail: $Actor"
    }
    $resourceTokens = @(
        Get-ArgumentValue -Arguments $Arguments -Name '--bucket'
        Get-ArgumentValue -Arguments $Arguments -Name '--key'
        Get-ArgumentValue -Arguments $Arguments -Name '--version-id'
        Get-ArgumentValue -Arguments $Arguments -Name '--key-id'
        Get-ArgumentValue -Arguments $Arguments -Name '--role-name'
        Get-ArgumentValue -Arguments $Arguments -Name '--alias-name'
        Get-ArgumentValue -Arguments $Arguments -Name '--name'
        Get-ArgumentValue -Arguments $Arguments -Name '--trail-name'
        Get-ArgumentValue -Arguments $Arguments -Name '--policy-name'
        Get-ArgumentValue -Arguments $Arguments -Name '--target-key-id'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $eventName = Get-CloudTrailEventName -Operation $Arguments[1]
    $existing = @($script:ActiveState.ExpectedEvents | Where-Object CallId -eq $CallId |
        Select-Object -First 1)[0]
    if ($existing) {
        if ($existing.Item -ne $Item -or $existing.Actor -ne $Actor -or
            $existing.Action -ne $Action -or $existing.EventSource -ne $eventSource -or
            $existing.EventName -ne $eventName -or
            (ConvertTo-CanonicalJson -Value @($existing.ResourceTokens)) -ne
                (ConvertTo-CanonicalJson -Value @($resourceTokens))) {
            throw "O CallId determinístico $CallId colidiu com outra mutação."
        }
        if ($CompletedAt) {
            $existing.CompletedAt = $CompletedAt
            $existing.ExitCode = $ExitCode
            $existing.ErrorCode = $ObservedErrorCode
            $existing.EventTime = $null
            $existing.EventId = $null
            $existing.Status = 'completed'
        }
        else {
            $existing.StartedAt = $StartedAt
            $existing.CompletedAt = $null
            $existing.ExitCode = $null
            $existing.ErrorCode = $null
            $existing.EventTime = $null
            $existing.EventId = $null
            $existing.Status = 'intent'
        }
        if ($AttemptId) { $existing.AttemptId = $AttemptId }
        Save-State -State $script:ActiveState
        return $existing
    }

    $expectedEvent = [ordered]@{
        CallId = $CallId
        Item = $Item
        Actor = $Actor
        ActorRoleName = $actorRoleName
        Action = $Action
        EventSource = $eventSource
        EventName = $eventName
        ResourceTokens = $resourceTokens
        ExpectSuccess = $ExpectSuccess
        ErrorCode = $ObservedErrorCode
        AcceptedErrorCodes = if ($ExpectSuccess) { @() } else { $AcceptedErrorCodes }
        StartedAt = $StartedAt
        CompletedAt = $CompletedAt
        EventTime = $null
        EventId = $null
        ExitCode = $ExitCode
        AttemptId = $AttemptId
        Status = if ($CompletedAt) { 'completed' } else { 'intent' }
    }
    $script:ActiveState.ExpectedEvents = @($script:ActiveState.ExpectedEvents) + $expectedEvent
    Save-State -State $script:ActiveState
    $expectedEvent
}

function Get-DeterministicCallId {
    param(
        [Parameter(Mandatory = $true)][string] $Item,
        [Parameter(Mandatory = $true)][string] $Actor,
        [Parameter(Mandatory = $true)][string] $Action,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $identity = @($RunId, $Item, $Actor, $Action) + @($Arguments) |
        ConvertTo-Json -Compress
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))
    [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Invoke-MatrixCall {
    param(
        [Parameter(Mandatory = $true)][string] $Item,
        [Parameter(Mandatory = $true)][string] $Actor,
        [Parameter(Mandatory = $true)][string] $Action,
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][bool] $ExpectSuccess,
        [string[]] $ExpectedErrorCodes = @('AccessDenied', 'AccessDeniedException')
    )

    $callId = Get-DeterministicCallId -Item $Item -Actor $Actor -Action $Action `
        -Arguments $Arguments
    $existing = @($script:ActiveState.ExpectedEvents | Where-Object CallId -eq $callId |
        Select-Object -First 1)[0]
    if ($existing -and $existing.Status -eq 'completed') {
        if ($Item -eq '12' -and $existing.ExpectSuccess -and $existing.ExitCode -eq 0) {
            return [pscustomobject]@{ ExitCode = 0; Output = '' }
        }
        if ($Item -ne '12') {
            throw "A chamada $Actor/$Action já foi executada e não pode ser repetida."
        }
    }
    $startedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    $expectedIntent = Add-ExpectedCloudTrailEvent -CallId $callId -Item $Item -Actor $Actor `
        -Action $Action -Arguments $Arguments -ExpectSuccess $ExpectSuccess `
        -AcceptedErrorCodes $ExpectedErrorCodes -ObservedErrorCode $null `
        -StartedAt $startedAt -CompletedAt $null -ExitCode $null
    $mutationAttempt = $null
    if ($Item -eq '12') {
        Start-MutationIntent -State $script:ActiveState -OperationId "matrix:$callId" `
            -ActorRoleName $expectedIntent.ActorRoleName `
            -EventSource $expectedIntent.EventSource -EventName $expectedIntent.EventName `
            -ResourceTokens @($expectedIntent.ResourceTokens) | Out-Null
        $mutationAttempt = Start-MutationAttempt -State $script:ActiveState `
            -OperationId "matrix:$callId"
        $expectedIntent.AttemptId = $mutationAttempt.AttemptId
        $expectedIntent.StartedAt = $mutationAttempt.StartedAt
        Save-State -State $script:ActiveState
    }
    $result = Invoke-AwsSingleAttempt -Credential $Credential -AllowFailure -Arguments $Arguments
    $completedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    $errorCode = Get-AwsErrorCode -Output $result.Output

    $passed = if ($ExpectSuccess) {
        $result.ExitCode -eq 0
    }
    else {
        $result.ExitCode -ne 0 -and $errorCode -in $ExpectedErrorCodes
    }
    $verdict = if ($passed) { 'PASS' } else { 'FAIL' }

    Write-CommonEvent -Item $Item -Actor $Actor -Action $Action `
        -Verdict $verdict -ExitCode $result.ExitCode -ErrorCode $errorCode
    Write-RestrictedEvent -Entry ([ordered]@{
        Ts = $completedAt
        CallId = $callId
        Item = $Item
        Actor = $Actor
        Action = $Action
        Expected = if ($ExpectSuccess) { 'success' } else { 'denied' }
        ExitCode = $result.ExitCode
        ErrorCode = $errorCode
        Verdict = $verdict
        Output = $result.Output
    })
    Add-ExpectedCloudTrailEvent -CallId $callId -Item $Item -Actor $Actor `
        -Action $Action -Arguments $Arguments -ExpectSuccess $ExpectSuccess `
        -AcceptedErrorCodes $ExpectedErrorCodes -ObservedErrorCode $errorCode `
        -StartedAt $(if ($mutationAttempt) { $mutationAttempt.StartedAt } else { $startedAt }) `
        -CompletedAt $completedAt -ExitCode $result.ExitCode `
        -AttemptId $(if ($mutationAttempt) { $mutationAttempt.AttemptId } else { $null }) | Out-Null
    if ($Item -eq '12') {
        $attemptOutcome = if ($result.ExitCode -eq 0) {
            'succeeded'
        }
        else {
            Get-FailureDisposition -Output $result.Output -ExitCode $result.ExitCode
        }
        Complete-MutationAttempt -State $script:ActiveState `
            -OperationId "matrix:$callId" -AttemptId $mutationAttempt.AttemptId `
            -LocalOutcome $attemptOutcome -ExitCode $result.ExitCode `
            -ErrorCode $errorCode -CompletedAt $completedAt
        if ($result.ExitCode -eq 0) {
            Complete-Mutation -State $script:ActiveState -OperationId "matrix:$callId" `
                -Status 'succeeded' -CompletedAt $completedAt
        }
        elseif (Test-AllMutationAttemptsDefinitelyFailed -State $script:ActiveState `
            -OperationId "matrix:$callId") {
            Complete-Mutation -State $script:ActiveState -OperationId "matrix:$callId" `
                -Status 'not-applied' -CompletedAt $completedAt
        }
    }
    if (-not $passed) {
        throw "Item $Item falhou em $Actor/$Action. Código observado: $errorCode. Saída: $($result.Output)"
    }

    $result
}

function Invoke-ProfileMatrixCall {
    param(
        [Parameter(Mandatory = $true)][string] $Item,
        [Parameter(Mandatory = $true)][string] $Action,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][bool] $ExpectSuccess,
        [string[]] $ExpectedErrorCodes = @('AccessDenied', 'AccessDeniedException')
    )

    $actor = 'Administrator'
    $callId = Get-DeterministicCallId -Item $Item -Actor $actor -Action $Action `
        -Arguments $Arguments
    $existing = @($script:ActiveState.ExpectedEvents | Where-Object CallId -eq $callId |
        Select-Object -First 1)[0]
    if ($existing -and $existing.Status -eq 'completed') {
        throw "A chamada $actor/$Action já foi executada e não pode ser repetida."
    }
    $startedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    Add-ExpectedCloudTrailEvent -CallId $callId -Item $Item -Actor $actor `
        -Action $Action -Arguments $Arguments -ExpectSuccess $ExpectSuccess `
        -AcceptedErrorCodes $ExpectedErrorCodes -ObservedErrorCode $null `
        -StartedAt $startedAt -CompletedAt $null -ExitCode $null | Out-Null
    $result = Invoke-ProfileAwsSingleAttempt -Arguments $Arguments
    $completedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    $errorCode = Get-AwsErrorCode -Output $result.Output
    $passed = if ($ExpectSuccess) {
        $result.ExitCode -eq 0
    }
    else {
        $result.ExitCode -ne 0 -and $errorCode -in $ExpectedErrorCodes
    }
    $verdict = if ($passed) { 'PASS' } else { 'FAIL' }
    Write-CommonEvent -Item $Item -Actor $actor -Action $Action `
        -Verdict $verdict -ExitCode $result.ExitCode -ErrorCode $errorCode
    Write-RestrictedEvent -Entry ([ordered]@{
        Ts = $completedAt
        CallId = $callId
        Item = $Item
        Actor = $actor
        Action = $Action
        Expected = if ($ExpectSuccess) { 'success' } else { 'denied' }
        ExitCode = $result.ExitCode
        ErrorCode = $errorCode
        Verdict = $verdict
        Output = $result.Output
    })
    Add-ExpectedCloudTrailEvent -CallId $callId -Item $Item -Actor $actor `
        -Action $Action -Arguments $Arguments -ExpectSuccess $ExpectSuccess `
        -AcceptedErrorCodes $ExpectedErrorCodes -ObservedErrorCode $errorCode `
        -StartedAt $startedAt -CompletedAt $completedAt -ExitCode $result.ExitCode | Out-Null
    if (-not $passed) {
        throw "Item $Item falhou em $actor/$Action. Código observado: $errorCode. Saída: $($result.Output)"
    }
    $result
}

function Add-TestResult {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][int] $Item,
        [Parameter(Mandatory = $true)][string] $Verdict,
        [Parameter(Mandatory = $true)][string] $Evidence
    )

    $withoutCurrent = @($State.Tests | Where-Object { $_.Item -ne $Item })
    $State.Tests = $withoutCurrent + [ordered]@{
        Item = $Item
        Verdict = $Verdict
        Evidence = $Evidence
        At = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    }
    Save-State -State $State
}

function Get-FileIntegrity {
    param([Parameter(Mandatory = $true)][string] $Path)

    [ordered]@{
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        Length = (Get-Item -LiteralPath $Path).Length
    }
}

function Assert-Integrity {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Expected,
        [Parameter(Mandatory = $true)][string] $ActualPath,
        [Parameter(Mandatory = $true)][string] $Message
    )

    $actual = Get-FileIntegrity -Path $ActualPath
    if ($actual.Sha256 -ne $Expected.Sha256 -or $actual.Length -ne $Expected.Length) {
        throw $Message
    }
}

function Invoke-ValidatedReadWorkflow {
    param(
        [Parameter(Mandatory = $true)][string] $Item,
        [Parameter(Mandatory = $true)][string] $Actor,
        [Parameter(Mandatory = $true)][string] $Action,
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][bool] $ExpectReadSuccess,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $ExpectedIntegrity,
        [Parameter(Mandatory = $true)][string] $OutputPath,
        [Parameter(Mandatory = $true)][scriptblock] $DownstreamCallback,
        [Parameter(Mandatory = $true)][string] $CorrelationId,
        [switch] $MutateCallbackBeforeValidation
    )

    if ($MutateCallbackBeforeValidation) {
        & $DownstreamCallback $CorrelationId
    }
    Invoke-MatrixCall -Item $Item -Actor $Actor -Action $Action `
        -Credential $Credential -ExpectSuccess $ExpectReadSuccess `
        -ExpectedErrorCodes @('KMS.DisabledException', 'DisabledException') `
        -Arguments $Arguments | Out-Null
    if (-not $ExpectReadSuccess) {
        return
    }
    Assert-Integrity -Expected $ExpectedIntegrity -ActualPath $OutputPath `
        -Message 'O conteúdo divergiu antes do callback downstream.'
    if (-not $MutateCallbackBeforeValidation) {
        & $DownstreamCallback $CorrelationId
    }
}

function Wait-KeyState {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string] $KeyArn,
        [Parameter(Mandatory = $true)][string] $ExpectedState
    )

    for ($attempt = 1; $attempt -le 24; $attempt++) {
        $description = Invoke-AwsJson -Credential $Credential -Arguments @(
            'kms', 'describe-key', '--key-id', $KeyArn, '--region', $Region, '--output', 'json'
        )
        if ($description.KeyMetadata.KeyState -eq $ExpectedState) {
            return
        }
        Start-Sleep -Seconds 5
    }
    throw "A CMK não alcançou o estado $ExpectedState."
}

if ($Phase -eq 'Execute') {
    Initialize-StateStorage
    $state = Read-State
    $script:ActiveState = $state
    if ($state.AccountId -ne $ExpectedAccountId -or $state.RunId -ne $RunId) {
        throw 'O estado restrito não pertence à conta e ao RunId autorizados.'
    }
    if ($state.Status -ne 'provisioned-not-verified') {
        throw "Execute não aceita o estado $($state.Status)."
    }
    if ([System.DateTimeOffset]::UtcNow -gt
        (ConvertFrom-IsoTimestamp -Value $state.ExpiresAt)) {
        throw 'O prazo operacional expirou; execute Cleanup e solicite um novo RunId.'
    }
    $readyAfter = ConvertFrom-IsoTimestamp -Value $state.ReadyAfter
    if ([System.DateTimeOffset]::UtcNow -lt $readyAfter) {
        [pscustomobject]@{
            Status = 'waiting-versioning-propagation'
            ReadyAfter = ConvertTo-CanonicalUtcTimestamp -Value $readyAfter
        } | ConvertTo-Json -Compress
        exit 3
    }

    $state.Status = 'exercise-running'
    $state.ExerciseStartedAt = ConvertTo-CanonicalUtcTimestamp -Value (
        [System.DateTimeOffset]::UtcNow
    )
    Save-State -State $state

    $credentials = [ordered]@{}
    foreach ($roleEntry in $roles.GetEnumerator()) {
        $credentials[$roleEntry.Key] = Get-DataCredential -OperatorCredential $operatorCredential `
            -RoleArn $roleEntry.Value.Arn -SessionName "matrix-$($roleEntry.Key.ToLowerInvariant())"
    }

    $bodyA = "body-$RunId-A"
    $bodyB = "body-$RunId-B"
    if ($bodyA.Length -ne $bodyB.Length) {
        throw 'Os corpos do experimento devem ter o mesmo comprimento.'
    }
    $originalNameSentinel = "original-$RunId-secret.txt"
    $bodyAPath = Join-Path $StateRoot 'body-a.bin'
    $bodyBPath = Join-Path $StateRoot 'body-b.bin'
    [System.IO.File]::WriteAllText($bodyAPath, $bodyA, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($bodyBPath, $bodyB, [System.Text.UTF8Encoding]::new($false))
    $integrityA = Get-FileIntegrity -Path $bodyAPath
    $integrityB = Get-FileIntegrity -Path $bodyBPath
    $contextAJson = '{"application":"app-a"}'
    $contextBJson = '{"application":"app-b"}'
    $contextA = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($contextAJson))
    $contextB = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($contextBJson))
    $identityKey = "app-a/identity/$([guid]::NewGuid().ToString('N'))"
    $controlBKey = "app-b/control/$([guid]::NewGuid().ToString('N'))"

    $state.Sentinels = [ordered]@{
        BodyA = $bodyA
        BodyB = $bodyB
        BodyABase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bodyA))
        BodyBBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bodyB))
        OriginalName = $originalNameSentinel
        Sha256A = $integrityA.Sha256
        Sha256B = $integrityB.Sha256
        Sha256ABase64 = [Convert]::ToBase64String([Convert]::FromHexString($integrityA.Sha256))
        Sha256BBase64 = [Convert]::ToBase64String([Convert]::FromHexString($integrityB.Sha256))
    }
    $state.Objects = [ordered]@{
        IdentityKey = $identityKey
        ControlBKey = $controlBKey
    }
    Save-State -State $state

    try {
        $putV1 = Invoke-MatrixCall -Item '1' -Actor 'UploadA' -Action 'PutObjectV1' `
            -Credential $credentials.UploadA -ExpectSuccess $true -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA,
                '--checksum-algorithm', 'CRC32', '--output', 'json'
            )
        $v1 = ($putV1.Output | ConvertFrom-Json).VersionId
        if ([string]::IsNullOrWhiteSpace($v1)) { throw 'PutObject V1 não retornou VersionId.' }

        $putV2 = Invoke-MatrixCall -Item '1' -Actor 'UploadA' -Action 'PutObjectV2' `
            -Credential $credentials.UploadA -ExpectSuccess $true -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--body', $bodyBPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA,
                '--checksum-algorithm', 'CRC32', '--output', 'json'
            )
        $v2 = ($putV2.Output | ConvertFrom-Json).VersionId
        if ([string]::IsNullOrWhiteSpace($v2) -or $v2 -eq $v1) { throw 'PutObject V2 não retornou uma nova versão.' }

        $putB = Invoke-MatrixCall -Item '1' -Actor 'UploadB' -Action 'PutObjectControlB' `
            -Credential $credentials.UploadB -ExpectSuccess $true -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $controlBKey,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyBArn, '--ssekms-encryption-context', $contextB,
                '--checksum-algorithm', 'CRC32', '--output', 'json'
            )
        $controlBVersion = ($putB.Output | ConvertFrom-Json).VersionId

        $crossPrefixKey = "app-b/negative/$([guid]::NewGuid().ToString('N'))"
        Invoke-MatrixCall -Item '1' -Actor 'UploadA' -Action 'PutObjectPrefixB' `
            -Credential $credentials.UploadA -ExpectSuccess $false -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket,
                '--key', $crossPrefixKey,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA
            ) | Out-Null
        Invoke-MatrixCall -Item '1' -Actor 'Operator' -Action 'VerifyCrossPrefixAbsent' `
            -Credential $operatorCredential -ExpectSuccess $false `
            -ExpectedErrorCodes @('404', 'NoSuchKey') -Arguments @(
                's3api', 'head-object', '--bucket', $state.ObjectBucket, '--key', $crossPrefixKey
            ) | Out-Null
        Invoke-MatrixCall -Item '1' -Actor 'UploadA' -Action 'GetObject' `
            -Credential $credentials.UploadA -ExpectSuccess $false -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                (Join-Path $StateRoot 'forbidden-upload-current.bin')
            ) | Out-Null
        Invoke-MatrixCall -Item '1' -Actor 'UploadA' -Action 'GetObjectVersion' `
            -Credential $credentials.UploadA -ExpectSuccess $false -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, (Join-Path $StateRoot 'forbidden-upload-v1.bin')
            ) | Out-Null
        Invoke-MatrixCall -Item '1' -Actor 'UploadA' -Action 'DeleteObjectVersion' `
            -Credential $credentials.UploadA -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $identityKey, '--version-id', $v1
            ) | Out-Null
        Invoke-MatrixCall -Item '1' -Actor 'Operator' -Action 'VerifyV1AfterDeniedDelete' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                's3api', 'head-object', '--bucket', $state.ObjectBucket,
                '--key', $identityKey, '--version-id', $v1
            ) | Out-Null
        Add-TestResult -State $state -Item 1 -Verdict 'PASS' -Evidence 'Operações isoladas de upload, com leituras e exclusão negadas.'

        $validatorV1Path = Join-Path $StateRoot 'validator-v1.bin'
        Invoke-MatrixCall -Item '2' -Actor 'ValidatorA' -Action 'GetObjectVersionV1' `
            -Credential $credentials.ValidatorA -ExpectSuccess $true -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, $validatorV1Path, '--output', 'json'
            ) | Out-Null
        Assert-Integrity -Expected $integrityA -ActualPath $validatorV1Path `
            -Message 'ValidatorA não recuperou integralmente V1.'
        $validatorNegativeKey = "app-a/validator-negative/$([guid]::NewGuid().ToString('N'))"
        Invoke-MatrixCall -Item '2' -Actor 'ValidatorA' -Action 'PutObject' `
            -Credential $credentials.ValidatorA -ExpectSuccess $false -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket,
                '--key', $validatorNegativeKey, '--body', $bodyAPath
            ) | Out-Null
        Invoke-MatrixCall -Item '2' -Actor 'Operator' -Action 'VerifyValidatorWriteAbsent' `
            -Credential $operatorCredential -ExpectSuccess $false `
            -ExpectedErrorCodes @('404', 'NoSuchKey') -Arguments @(
                's3api', 'head-object', '--bucket', $state.ObjectBucket, '--key', $validatorNegativeKey
            ) | Out-Null
        Invoke-MatrixCall -Item '2' -Actor 'ValidatorA' -Action 'DeleteObjectVersion' `
            -Credential $credentials.ValidatorA -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $identityKey, '--version-id', $v1
            ) | Out-Null
        Add-TestResult -State $state -Item 2 -Verdict 'PASS' -Evidence 'V1 íntegra; escrita e exclusão negadas.'

        $disposableKey = "app-a/disposable/$([guid]::NewGuid().ToString('N'))"
        $disposablePut = Invoke-MatrixCall -Item '3' -Actor 'UploadA' -Action 'PutDisposable' `
            -Credential $credentials.UploadA -ExpectSuccess $true -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $disposableKey,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA,
                '--output', 'json'
            )
        $disposableVersion = ($disposablePut.Output | ConvertFrom-Json).VersionId
        Invoke-MatrixCall -Item '3' -Actor 'DisposerA' -Action 'GetDisposable' `
            -Credential $credentials.DisposerA -ExpectSuccess $false -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $disposableKey,
                '--version-id', $disposableVersion, (Join-Path $StateRoot 'forbidden-disposer.bin')
            ) | Out-Null
        Invoke-MatrixCall -Item '3' -Actor 'DisposerA' -Action 'DeleteDisposableVersion' `
            -Credential $credentials.DisposerA -ExpectSuccess $true -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $disposableKey, '--version-id', $disposableVersion
            ) | Out-Null
        Invoke-MatrixCall -Item '3' -Actor 'Operator' -Action 'VerifyDisposableAbsent' `
            -Credential $operatorCredential -ExpectSuccess $false `
            -ExpectedErrorCodes @('404', 'NoSuchKey', 'NoSuchVersion') -Arguments @(
                's3api', 'head-object', '--bucket', $state.ObjectBucket,
                '--key', $disposableKey, '--version-id', $disposableVersion
            ) | Out-Null
        Add-TestResult -State $state -Item 3 -Verdict 'PASS' -Evidence 'Disposer sem leitura removeu somente a versão descartável.'

        Invoke-MatrixCall -Item '4' -Actor 'DispatchSynthetic' -Action 'HeadObject' `
            -Credential $credentials.DispatchSynthetic -ExpectSuccess $false -Arguments @(
                's3api', 'head-object', '--bucket', $state.ObjectBucket, '--key', $identityKey
            ) | Out-Null
        Invoke-MatrixCall -Item '4' -Actor 'DispatchSynthetic' -Action 'GetObjectVersion' `
            -Credential $credentials.DispatchSynthetic -ExpectSuccess $false -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, (Join-Path $StateRoot 'forbidden-dispatch.bin')
            ) | Out-Null
        $dispatchNegativeKey = "app-a/dispatch-negative/$([guid]::NewGuid().ToString('N'))"
        Invoke-MatrixCall -Item '4' -Actor 'DispatchSynthetic' -Action 'PutObject' `
            -Credential $credentials.DispatchSynthetic -ExpectSuccess $false -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket,
                '--key', $dispatchNegativeKey, '--body', $bodyAPath
            ) | Out-Null
        Invoke-MatrixCall -Item '4' -Actor 'Operator' -Action 'VerifyDispatchWriteAbsent' `
            -Credential $operatorCredential -ExpectSuccess $false `
            -ExpectedErrorCodes @('404', 'NoSuchKey') -Arguments @(
                's3api', 'head-object', '--bucket', $state.ObjectBucket, '--key', $dispatchNegativeKey
            ) | Out-Null
        Invoke-MatrixCall -Item '4' -Actor 'DispatchSynthetic' -Action 'DeleteObjectVersion' `
            -Credential $credentials.DispatchSynthetic -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $identityKey, '--version-id', $v1
            ) | Out-Null
        Invoke-MatrixCall -Item '4' -Actor 'DispatchSynthetic' -Action 'DescribeKey' `
            -Credential $credentials.DispatchSynthetic -ExpectSuccess $false -Arguments @(
                'kms', 'describe-key', '--key-id', $state.KeyAArn, '--region', $Region
            ) | Out-Null
        Add-TestResult -State $state -Item 4 -Verdict 'PASS' -Evidence 'Dispatch sem acesso S3 ou KMS.'

        $currentPath = Join-Path $StateRoot 'current-v2.bin'
        $currentRead = Invoke-MatrixCall -Item '5' -Actor 'CurrentVersionProbe' -Action 'GetCurrentVersion' `
            -Credential $credentials.CurrentVersionProbe -ExpectSuccess $true -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                $currentPath, '--output', 'json'
            )
        $currentVersion = ($currentRead.Output | ConvertFrom-Json).VersionId
        if ($currentVersion -ne $v2) { throw 'A leitura corrente não retornou V2.' }
        Assert-Integrity -Expected $integrityB -ActualPath $currentPath -Message 'A leitura corrente não corresponde a V2.'
        $fixedV1Path = Join-Path $StateRoot 'fixed-v1.bin'
        Invoke-MatrixCall -Item '5' -Actor 'ValidatorA' -Action 'GetFixedV1' `
            -Credential $credentials.ValidatorA -ExpectSuccess $true -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, $fixedV1Path, '--output', 'json'
            ) | Out-Null
        Assert-Integrity -Expected $integrityA -ActualPath $fixedV1Path -Message 'A versão V1 fixada divergiu.'
        Add-TestResult -State $state -Item 5 -Verdict 'PASS' -Evidence 'Leitura corrente de V2 e leitura explícita de V1 preservadas.'

        $negativeActors = @(
            [ordered]@{ Name = 'UploadA'; Credential = $credentials.UploadA },
            [ordered]@{ Name = 'UploadB'; Credential = $credentials.UploadB },
            [ordered]@{ Name = 'ValidatorA'; Credential = $credentials.ValidatorA },
            [ordered]@{ Name = 'CurrentVersionProbe'; Credential = $credentials.CurrentVersionProbe },
            [ordered]@{ Name = 'DispatchSynthetic'; Credential = $credentials.DispatchSynthetic }
        )
        foreach ($actor in $negativeActors) {
            $canaryKey = "app-a/negative/$([guid]::NewGuid().ToString('N'))"
            $canaryPut = Invoke-MatrixCall -Item '6' -Actor 'UploadA' -Action "CreateCanaryFor$($actor.Name)" `
                -Credential $credentials.UploadA -ExpectSuccess $true -Arguments @(
                    's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $canaryKey,
                    '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                    '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA,
                    '--output', 'json'
                )
            $canaryVersion = ($canaryPut.Output | ConvertFrom-Json).VersionId
            Invoke-MatrixCall -Item '6' -Actor $actor.Name -Action 'DeleteExistingVersion' `
                -Credential $actor.Credential -ExpectSuccess $false -Arguments @(
                    's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                    '--key', $canaryKey, '--version-id', $canaryVersion
                ) | Out-Null
            Invoke-MatrixCall -Item '6' -Actor 'Operator' -Action 'VerifyCanaryPresent' `
                -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                    's3api', 'head-object', '--bucket', $state.ObjectBucket,
                    '--key', $canaryKey, '--version-id', $canaryVersion
                ) | Out-Null
        }
        Add-TestResult -State $state -Item 6 -Verdict 'PASS' -Evidence 'Cada role normal falhou ao excluir uma versão existente.'

        $wrongPrefixKey = "app-b/wrong-prefix/$([guid]::NewGuid().ToString('N'))"
        $wrongKeyObject = "app-a/wrong-key/$([guid]::NewGuid().ToString('N'))"
        $wrongContextKey = "app-a/wrong-context/$([guid]::NewGuid().ToString('N'))"
        Invoke-MatrixCall -Item '7' -Actor 'UploadA' -Action 'WrongPrefix' `
            -Credential $credentials.UploadA -ExpectSuccess $false -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket,
                '--key', $wrongPrefixKey,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA
            ) | Out-Null
        Invoke-MatrixCall -Item '7' -Actor 'UploadA' -Action 'WrongKey' `
            -Credential $credentials.UploadA -ExpectSuccess $false `
            -ExpectedErrorCodes @('AccessDenied', 'AccessDeniedException', 'KMS.AccessDeniedException') -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket,
                '--key', $wrongKeyObject,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyBArn, '--ssekms-encryption-context', $contextA
            ) | Out-Null
        Invoke-MatrixCall -Item '7' -Actor 'UploadA' -Action 'WrongEncryptionContext' `
            -Credential $credentials.UploadA -ExpectSuccess $false `
            -ExpectedErrorCodes @('AccessDenied', 'AccessDeniedException', 'KMS.AccessDeniedException') -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket,
                '--key', $wrongContextKey,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextB
            ) | Out-Null
        foreach ($negativeKey in @($wrongPrefixKey, $wrongKeyObject, $wrongContextKey)) {
            Invoke-MatrixCall -Item '7' -Actor 'Operator' -Action 'VerifyDeniedWriteAbsent' `
                -Credential $operatorCredential -ExpectSuccess $false `
                -ExpectedErrorCodes @('404', 'NoSuchKey') -Arguments @(
                    's3api', 'head-object', '--bucket', $state.ObjectBucket, '--key', $negativeKey
                ) | Out-Null
        }
        Add-TestResult -State $state -Item 7 -Verdict 'PASS' -Evidence 'Prefixo, CMK e contexto incompatíveis negados.'

        $downstreamTelemetry = [System.Collections.Generic.List[object]]::new()
        $downstreamCallback = {
            param([Parameter(Mandatory = $true)][string] $CorrelationId)

            $entry = [ordered]@{
                Ts = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
                Type = 'instrumented-downstream-callback'
                CorrelationId = $CorrelationId
            }
            $downstreamTelemetry.Add($entry)
            Write-RestrictedEvent -Entry $entry
        }
        Invoke-MatrixCall -Item '8' -Actor 'Operator' -Action 'DisableKeyA' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                'kms', 'disable-key', '--key-id', $state.KeyAArn, '--region', $Region
            ) | Out-Null
        Wait-KeyState -Credential $operatorCredential -KeyArn $state.KeyAArn -ExpectedState 'Disabled'
        $disabledPath = Join-Path $StateRoot 'disabled-key-v1.bin'
        Invoke-ValidatedReadWorkflow -Item '8' -Actor 'ValidatorA' `
            -Action 'GetV1WithDisabledKey' -Credential $credentials.ValidatorA `
            -ExpectReadSuccess $false -ExpectedIntegrity $integrityA `
            -OutputPath $disabledPath -DownstreamCallback $downstreamCallback `
            -CorrelationId "item8-$RunId-denied" -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, $disabledPath
            )
        if ($downstreamTelemetry.Count -ne 0) {
            throw 'O callback downstream foi chamado apesar da falha KMS.'
        }
        $mutantTelemetry = [System.Collections.Generic.List[object]]::new()
        $mutantCallback = {
            param([Parameter(Mandatory = $true)][string] $CorrelationId)
            $mutantTelemetry.Add([ordered]@{ CorrelationId = $CorrelationId })
        }
        $mutantPath = Join-Path $StateRoot 'disabled-key-mutant-v1.bin'
        Invoke-ValidatedReadWorkflow -Item '8' -Actor 'ValidatorA' `
            -Action 'GetV1WithDisabledKeyMutant' -Credential $credentials.ValidatorA `
            -ExpectReadSuccess $false -ExpectedIntegrity $integrityA `
            -OutputPath $mutantPath -DownstreamCallback $mutantCallback `
            -CorrelationId "item8-$RunId-mutant" -MutateCallbackBeforeValidation `
            -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, $mutantPath
            )
        if ($mutantTelemetry.Count -ne 1) {
            throw 'O oráculo não detectou o mutante que antecipa o callback downstream.'
        }
        Invoke-MatrixCall -Item '8' -Actor 'Operator' -Action 'EnableKeyA' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                'kms', 'enable-key', '--key-id', $state.KeyAArn, '--region', $Region
            ) | Out-Null
        Wait-KeyState -Credential $operatorCredential -KeyArn $state.KeyAArn -ExpectedState 'Enabled'
        $enabledPath = Join-Path $StateRoot 'enabled-key-v1.bin'
        Invoke-ValidatedReadWorkflow -Item '8' -Actor 'ValidatorA' `
            -Action 'GetV1WithEnabledKey' -Credential $credentials.ValidatorA `
            -ExpectReadSuccess $true -ExpectedIntegrity $integrityA `
            -OutputPath $enabledPath -DownstreamCallback $downstreamCallback `
            -CorrelationId "item8-$RunId-control" -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, $enabledPath, '--output', 'json'
            )
        if ($downstreamTelemetry.Count -ne 1 -or
            $downstreamTelemetry[0].CorrelationId -ne "item8-$RunId-control") {
            throw 'O controle positivo não registrou exatamente um callback downstream correlacionado.'
        }
        Add-TestResult -State $state -Item 8 -Verdict 'PASS' `
            -Evidence 'O mesmo fluxo falhou fechado com KMS indisponível, liberou o callback após validação no controle positivo e matou o mutante que antecipava o callback.'

        Invoke-MatrixCall -Item '9' -Actor 'Operator' -Action 'RotateKeyOnDemand' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                'kms', 'rotate-key-on-demand', '--key-id', $state.KeyAArn, '--region', $Region
            ) | Out-Null
        $rotationObserved = $false
        for ($attempt = 1; $attempt -le 24; $attempt++) {
            $rotations = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
                'kms', 'list-key-rotations', '--key-id', $state.KeyAArn,
                '--region', $Region, '--output', 'json'
            )
            if (@($rotations.Rotations | Where-Object RotationType -eq 'ON_DEMAND').Count -gt 0) {
                $rotationObserved = $true
                break
            }
            Start-Sleep -Seconds 5
        }
        if (-not $rotationObserved) { throw 'A rotação ON_DEMAND não foi observada.' }
        $postRotationKey = "app-a/identity/$([guid]::NewGuid().ToString('N'))"
        $postRotationPut = Invoke-MatrixCall -Item '9' -Actor 'UploadA' -Action 'PutAfterRotation' `
            -Credential $credentials.UploadA -ExpectSuccess $true -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $postRotationKey,
                '--body', $bodyBPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA,
                '--output', 'json'
            )
        $postRotationVersion = ($postRotationPut.Output | ConvertFrom-Json).VersionId
        $postRotationPath = Join-Path $StateRoot 'post-rotation.bin'
        Invoke-MatrixCall -Item '9' -Actor 'ValidatorA' -Action 'ReadAfterRotation' `
            -Credential $credentials.ValidatorA -ExpectSuccess $true -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $postRotationKey,
                '--version-id', $postRotationVersion, $postRotationPath, '--output', 'json'
            ) | Out-Null
        Assert-Integrity -Expected $integrityB -ActualPath $postRotationPath -Message 'O objeto pós-rotação divergiu.'
        $oldAfterRotationPath = Join-Path $StateRoot 'old-after-rotation.bin'
        Invoke-MatrixCall -Item '9' -Actor 'ValidatorA' -Action 'ReadV1AfterRotation' `
            -Credential $credentials.ValidatorA -ExpectSuccess $true -Arguments @(
                's3api', 'get-object', '--bucket', $state.ObjectBucket, '--key', $identityKey,
                '--version-id', $v1, $oldAfterRotationPath, '--output', 'json'
            ) | Out-Null
        Assert-Integrity -Expected $integrityA -ActualPath $oldAfterRotationPath -Message 'V1 divergiu após rotação.'
        Add-TestResult -State $state -Item 9 -Verdict 'PASS' -Evidence 'Rotação on-demand preservou materiais anterior e novo.'

        $governanceKey = "app-a/locks/$([guid]::NewGuid().ToString('N'))"
        $complianceKey = "app-a/locks/$([guid]::NewGuid().ToString('N'))"
        $governancePut = Invoke-MatrixCall -Item '10' -Actor 'UploadA' -Action 'PutGovernanceCanary' `
            -Credential $credentials.UploadA -ExpectSuccess $true -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $governanceKey,
                '--body', $bodyAPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA,
                '--output', 'json'
            )
        $compliancePut = Invoke-MatrixCall -Item '10' -Actor 'UploadA' -Action 'PutComplianceCanary' `
            -Credential $credentials.UploadA -ExpectSuccess $true -Arguments @(
                's3api', 'put-object', '--bucket', $state.ObjectBucket, '--key', $complianceKey,
                '--body', $bodyBPath, '--server-side-encryption', 'aws:kms',
                '--ssekms-key-id', $state.KeyAArn, '--ssekms-encryption-context', $contextA,
                '--output', 'json'
            )
        $governanceVersion = ($governancePut.Output | ConvertFrom-Json).VersionId
        $complianceVersion = ($compliancePut.Output | ConvertFrom-Json).VersionId
        $retainUntil = [System.DateTimeOffset]::UtcNow.AddMinutes(12)
        $governanceRetention = [ordered]@{
            Mode = 'GOVERNANCE'
            RetainUntilDate = ConvertTo-CanonicalUtcTimestamp -Value $retainUntil
        } | ConvertTo-Json -Compress
        $complianceRetention = [ordered]@{
            Mode = 'COMPLIANCE'
            RetainUntilDate = ConvertTo-CanonicalUtcTimestamp -Value $retainUntil
        } | ConvertTo-Json -Compress
        $excessiveRetention = [ordered]@{
            Mode = 'GOVERNANCE'
            RetainUntilDate = ConvertTo-CanonicalUtcTimestamp -Value (
                [System.DateTimeOffset]::UtcNow.AddDays(2)
            )
        } | ConvertTo-Json -Compress
        Invoke-ProfileMatrixCall -Item '10' -Action 'RejectExcessiveGovernanceRetention' `
            -ExpectSuccess $false -Arguments @(
                's3api', 'put-object-retention', '--profile', $Profile,
                '--bucket', $state.ObjectBucket, '--key', $governanceKey,
                '--version-id', $governanceVersion, '--retention', $excessiveRetention,
                '--expected-bucket-owner', $ExpectedAccountId
            ) | Out-Null
        $rejectedRetentionRead = Invoke-ProfileMatrixCall -Item '10' `
            -Action 'ConfirmExcessiveGovernanceRetentionAbsent' -ExpectSuccess $true `
            -Arguments @(
                's3api', 'get-object-retention', '--profile', $Profile,
                '--bucket', $state.ObjectBucket, '--key', $governanceKey,
                '--version-id', $governanceVersion,
                '--expected-bucket-owner', $ExpectedAccountId, '--output', 'json'
            )
        $rejectedRetention = $rejectedRetentionRead.Output |
            ConvertFrom-Json -DateKind String
        if ($rejectedRetention.Retention.Mode -or $rejectedRetention.Retention.RetainUntilDate) {
            throw 'A retenção acima do teto foi aplicada apesar da negação esperada.'
        }
        Invoke-MatrixCall -Item '10' -Actor 'DisposerA' -Action 'RejectAnyRetention' `
            -Credential $credentials.DisposerA -ExpectSuccess $false -Arguments @(
                's3api', 'put-object-retention', '--bucket', $state.ObjectBucket,
                '--key', $governanceKey, '--version-id', $governanceVersion,
                '--retention', $governanceRetention,
                '--expected-bucket-owner', $ExpectedAccountId
            ) | Out-Null
        Invoke-ProfileMatrixCall -Item '10' -Action 'SetGovernanceRetention' `
            -ExpectSuccess $true -Arguments @(
                's3api', 'put-object-retention', '--profile', $Profile,
                '--bucket', $state.ObjectBucket, '--key', $governanceKey,
                '--version-id', $governanceVersion, '--retention', $governanceRetention,
                '--expected-bucket-owner', $ExpectedAccountId
            ) | Out-Null
        Invoke-ProfileMatrixCall -Item '10' -Action 'SetComplianceRetention' `
            -ExpectSuccess $true -Arguments @(
                's3api', 'put-object-retention', '--profile', $Profile,
                '--bucket', $state.ObjectBucket, '--key', $complianceKey,
                '--version-id', $complianceVersion, '--retention', $complianceRetention,
                '--expected-bucket-owner', $ExpectedAccountId
            ) | Out-Null
        $observedGovernance = Invoke-MatrixCall -Item '10' -Actor 'DisposerA' `
            -Action 'ReadGovernanceRetention' -Credential $credentials.DisposerA `
            -ExpectSuccess $true -Arguments @(
                's3api', 'get-object-retention', '--bucket', $state.ObjectBucket,
                '--key', $governanceKey, '--version-id', $governanceVersion,
                '--output', 'json'
            )
        $observedCompliance = Invoke-MatrixCall -Item '10' -Actor 'DisposerA' `
            -Action 'ReadComplianceRetention' -Credential $credentials.DisposerA `
            -ExpectSuccess $true -Arguments @(
                's3api', 'get-object-retention', '--bucket', $state.ObjectBucket,
                '--key', $complianceKey, '--version-id', $complianceVersion,
                '--output', 'json'
            )
        $governanceDocument = $observedGovernance.Output |
            ConvertFrom-Json -DateKind String
        $complianceDocument = $observedCompliance.Output |
            ConvertFrom-Json -DateKind String
        $observedGovernanceUntil = ConvertFrom-IsoTimestamp -Value (
            $governanceDocument.Retention.RetainUntilDate
        )
        $observedComplianceUntil = ConvertFrom-IsoTimestamp -Value (
            $complianceDocument.Retention.RetainUntilDate
        )
        if ($governanceDocument.Retention.Mode -ne 'GOVERNANCE' -or
            $complianceDocument.Retention.Mode -ne 'COMPLIANCE' -or
            [Math]::Abs(($observedGovernanceUntil - $retainUntil).TotalSeconds) -gt 2 -or
            [Math]::Abs(($observedComplianceUntil - $retainUntil).TotalSeconds) -gt 2 -or
            $observedGovernanceUntil -gt [System.DateTimeOffset]::UtcNow.AddMinutes(15) -or
            $observedComplianceUntil -gt [System.DateTimeOffset]::UtcNow.AddMinutes(15)) {
            throw 'O modo ou o prazo de retenção divergiu do limite operacional autorizado.'
        }
        Invoke-MatrixCall -Item '10' -Actor 'DisposerA' -Action 'GovernanceWithoutHeader' `
            -Credential $credentials.DisposerA -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $governanceKey, '--version-id', $governanceVersion
            ) | Out-Null
        Invoke-MatrixCall -Item '10' -Actor 'DisposerA' -Action 'GovernanceHeaderWithoutPermission' `
            -Credential $credentials.DisposerA -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $governanceKey, '--version-id', $governanceVersion,
                '--bypass-governance-retention'
            ) | Out-Null
        Invoke-MatrixCall -Item '10' -Actor 'Operator' -Action 'GovernanceBypass' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $governanceKey, '--version-id', $governanceVersion,
                '--bypass-governance-retention'
            ) | Out-Null
        Invoke-MatrixCall -Item '10' -Actor 'DisposerA' -Action 'ComplianceWithoutBypass' `
            -Credential $credentials.DisposerA -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $complianceKey, '--version-id', $complianceVersion
            ) | Out-Null
        Invoke-MatrixCall -Item '10' -Actor 'DisposerA' -Action 'ComplianceWithBypass' `
            -Credential $credentials.DisposerA -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $complianceKey, '--version-id', $complianceVersion,
                '--bypass-governance-retention'
            ) | Out-Null
        Invoke-MatrixCall -Item '10' -Actor 'Operator' -Action 'ComplianceControllerBypass' `
            -Credential $operatorCredential -ExpectSuccess $false -Arguments @(
                's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                '--key', $complianceKey, '--version-id', $complianceVersion,
                '--bypass-governance-retention'
            ) | Out-Null
        Add-TestResult -State $state -Item 10 -Verdict 'PASS' `
            -Evidence 'Somente o administrador definiu retenção de 12 minutos; a role de dados não prolongou o prazo, Governance exigiu bypass autorizado e Compliance bloqueou todos os controladores.'

        $state.Objects.V1VersionId = $v1
        $state.Objects.V2VersionId = $v2
        $state.Objects.ControlBVersionId = $controlBVersion
        $state.Objects.PostRotationKey = $postRotationKey
        $state.Objects.PostRotationVersionId = $postRotationVersion
        $state.Objects.ComplianceKey = $complianceKey
        $state.Objects.ComplianceVersionId = $complianceVersion
        $state.Objects.ComplianceRetainUntil = ConvertTo-CanonicalUtcTimestamp `
            -Value $retainUntil
        $state.IntegrityA = $integrityA
        $state.IntegrityB = $integrityB
        $state.ExerciseCompletedAt = ConvertTo-CanonicalUtcTimestamp -Value (
            [System.DateTimeOffset]::UtcNow
        )
        $state.Status = 'exercised-awaiting-evidence'
        Save-State -State $state

        [pscustomobject]@{
            Status = $state.Status
            PassedItems = @($state.Tests | Where-Object Verdict -eq 'PASS').Count
            PendingItems = @(11, 12)
            ComplianceRetainUntil = $state.Objects.ComplianceRetainUntil
        } | ConvertTo-Json -Compress
    }
    catch {
        $state.Status = 'exercise-failed'
        $state.ExerciseFailure = $_.Exception.Message
        Save-State -State $state
        throw
    }
}

function Get-CloudTrailCorpus {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State
    )

    $downloadRoot = Join-Path $StateRoot 'cloudtrail'
    if (-not (Test-Path -LiteralPath $downloadRoot)) {
        New-Item -ItemType Directory -Path $downloadRoot | Out-Null
    }
    $listing = Invoke-AwsJson -Credential $Credential -Arguments @(
        's3api', 'list-objects-v2', '--bucket', $State.TrailBucket,
        '--prefix', "AWSLogs/$ExpectedAccountId/", '--output', 'json'
    )
    $objects = @($listing.Contents | Where-Object {
        $_.Key -match "/CloudTrail/$([regex]::Escape($Region))/.+\.json\.gz$" -or
        $_.Key -match "/CloudTrail-Digest/$([regex]::Escape($Region))/.+\.json\.gz$"
    })
    $keys = @($objects.Key)
    $logKeys = @($objects.Key | Where-Object { $_ -match '/CloudTrail/' })
    $digestKeys = @($objects.Key | Where-Object { $_ -match '/CloudTrail-Digest/' })
    $allText = [System.Text.StringBuilder]::new()
    $records = [System.Collections.Generic.List[object]]::new()
    $manifest = [System.Collections.Generic.List[object]]::new()

    foreach ($object in $objects) {
        $key = $object.Key
        $type = if ($key -match '/CloudTrail-Digest/') { 'digest' } else { 'log' }
        $localName = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key))).ToLowerInvariant() + '.json.gz'
        $localPath = Join-Path $downloadRoot $localName
        $temporaryDownloadPath = "$localPath.download"
        Invoke-Aws -Credential $Credential -Arguments @(
            's3api', 'get-object', '--bucket', $State.TrailBucket,
            '--key', $key, $temporaryDownloadPath, '--output', 'json'
        ) | Out-Null
        Move-Item -LiteralPath $temporaryDownloadPath -Destination $localPath -Force

        $integrity = Get-FileIntegrity -Path $localPath
        if ($integrity.Length -ne [long]$object.Size) {
            throw "O tamanho do objeto local do CloudTrail divergiu do registrado no S3: $key."
        }
        $manifest.Add([ordered]@{
            Key = $key
            Type = $type
            LocalPath = $localPath
            Sha256 = $integrity.Sha256
            Length = $integrity.Length
        })
        if ($type -eq 'digest') {
            continue
        }

        $fileStream = [System.IO.File]::OpenRead($localPath)
        try {
            $gzip = [System.IO.Compression.GZipStream]::new(
                $fileStream,
                [System.IO.Compression.CompressionMode]::Decompress
            )
            try {
                $reader = [System.IO.StreamReader]::new($gzip, [Text.Encoding]::UTF8)
                try {
                    $json = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $gzip.Dispose()
            }
        }
        finally {
            $fileStream.Dispose()
        }
        [void]$allText.AppendLine($json)
        $document = $json | ConvertFrom-Json -DateKind String
        foreach ($record in @($document.Records)) {
            $records.Add($record)
        }
    }

    $State.Evidence.CloudTrailManifest = @($manifest)
    Save-State -State $State

    [pscustomobject]@{
        Keys = $keys
        LogKeys = $logKeys
        DigestKeys = $digestKeys
        Manifest = @($manifest)
        Text = $allText.ToString()
        Records = $records
    }
}

function Assert-LocalCloudTrailManifest {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $State)

    $manifest = @($State.Evidence.CloudTrailManifest)
    if (@($manifest | Where-Object Type -eq 'log').Count -lt 1 -or
        @($manifest | Where-Object Type -eq 'digest').Count -lt 1) {
        throw 'O manifesto autenticado não contém ao menos um log e um digest do CloudTrail.'
    }
    foreach ($entry in $manifest) {
        if (-not (Test-Path -LiteralPath $entry.LocalPath -PathType Leaf)) {
            throw "O arquivo autenticado do CloudTrail não existe: $($entry.LocalPath)."
        }
        $integrity = Get-FileIntegrity -Path $entry.LocalPath
        if ($integrity.Sha256 -ne $entry.Sha256 -or $integrity.Length -ne $entry.Length) {
            throw "A integridade local do corpus CloudTrail falhou para $($entry.Key)."
        }
    }
}

function Get-CloudTrailCorrelationResult {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][object[]] $Records,
        [Parameter(Mandatory = $true)][object[]] $ExpectedEvents
    )

    $usedEventIds = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal
    )
    $missingCalls = [System.Collections.Generic.List[string]]::new()
    $correlations = [System.Collections.Generic.List[object]]::new()
    foreach ($expected in $ExpectedEvents) {
        $windowStart = (ConvertFrom-IsoTimestamp -Value $expected.StartedAt).AddMinutes(-2)
        $windowEnd = if ($expected.CompletedAt) {
            (ConvertFrom-IsoTimestamp -Value $expected.CompletedAt).AddMinutes(2)
        }
        else {
            (ConvertFrom-IsoTimestamp -Value $expected.StartedAt).AddMinutes(10)
        }
        if ($windowEnd -lt $windowStart) { $windowEnd = $windowStart }
        $candidates = @($Records | Where-Object {
            $eventTime = ConvertFrom-IsoTimestamp -Value $_.eventTime
            $actorMatches = $_.userIdentity.arn -like "*/$($expected.ActorRoleName)/*" -or
                $_.userIdentity.sessionContext.sessionIssuer.userName -eq $expected.ActorRoleName
            $outcomeMatches = if ($expected.ExpectSuccess) {
                [string]::IsNullOrWhiteSpace($_.errorCode)
            }
            else {
                $_.errorCode -in @($expected.AcceptedErrorCodes)
            }
            $eventJson = $_ | ConvertTo-Json -Depth 30 -Compress
            $resourceMatches = $true
            foreach ($token in @($expected.ResourceTokens)) {
                if (-not $eventJson.Contains($token, [StringComparison]::Ordinal)) {
                    $resourceMatches = $false
                    break
                }
            }
            $eventIdMatches = -not $expected.EventId -or $_.eventID -eq $expected.EventId
            $timeMatches = $expected.EventId -or
                ($eventTime -ge $windowStart -and $eventTime -le $windowEnd)
            -not $usedEventIds.Contains($_.eventID) -and $eventIdMatches -and
                $timeMatches -and $actorMatches -and
                $_.eventSource -eq $expected.EventSource -and
                $_.eventName -eq $expected.EventName -and $outcomeMatches -and
                $resourceMatches
        } | Sort-Object eventTime)
        $candidates = @($candidates | Group-Object eventID | ForEach-Object {
            $_.Group[0]
        })
        if ($candidates.Count -ne 1) {
            $missingCalls.Add($expected.CallId)
            continue
        }
        $matched = $candidates[0]
        $requiresMutationLedger = $expected.Item -eq '12' -or
            -not [string]::IsNullOrWhiteSpace([string]$expected.AttemptId)
        $matchedAttempt = $null
        if ($requiresMutationLedger) {
            $mutation = Get-Mutation -State $State -OperationId "matrix:$($expected.CallId)"
            if (-not $mutation) {
                $missingCalls.Add($expected.CallId)
                continue
            }
            $outcome = if ($expected.ExpectSuccess) { 'success' } else { 'failure' }
            $matchedEventTime = ConvertFrom-IsoTimestamp -Value $matched.eventTime
            $matchedAttempt = if ($expected.EventId) {
                $eventAttempts = @($mutation.Attempts | Where-Object {
                    $_.EventId -eq $expected.EventId -and
                        (($outcome -eq 'success' -and $_.LocalOutcome -ne 'failed-definitive') -or
                            ($outcome -eq 'failure' -and $_.LocalOutcome -eq 'failed-definitive'))
                })
                if ($eventAttempts.Count -eq 1) { $eventAttempts[0] } else { $null }
            }
            else {
                Get-CompatibleMutationAttempt -Attempts @($mutation.Attempts) `
                    -EventTime $matchedEventTime -Outcome $outcome
            }
            if (-not $matchedAttempt) {
                $missingCalls.Add($expected.CallId)
                continue
            }
        }
        [void]$usedEventIds.Add($matched.eventID)
        $correlations.Add([ordered]@{
            Ts = $matched.eventTime
            Type = 'cloudtrail-correlation'
            CallId = $expected.CallId
            AttemptId = if ($matchedAttempt) { $matchedAttempt.AttemptId } else { $null }
            Item = $expected.Item
            Actor = $expected.Actor
            EventSource = $matched.eventSource
            EventName = $matched.eventName
            RequestId = $matched.requestID
            EventId = $matched.eventID
            ErrorCode = $matched.errorCode
        })
    }

    [pscustomobject]@{
        MissingCallIds = @($missingCalls)
        Correlations = @($correlations)
    }
}

function Get-FinalHistoryCorrelationResult {
    param(
        [Parameter(Mandatory = $true)][psobject] $History,
        [Parameter(Mandatory = $true)][object[]] $ExpectedEvents
    )

    $usedEventIds = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal
    )
    $missingAttempts = [System.Collections.Generic.List[string]]::new()
    $correlations = [System.Collections.Generic.List[object]]::new()
    foreach ($expected in $ExpectedEvents) {
        $localStart = ConvertFrom-IsoTimestamp -Value $expected.StartedAt
        $windowStart = $localStart.AddMinutes(-2)
        $windowEnd = (ConvertFrom-IsoTimestamp -Value $expected.CompletedAt).AddMinutes(2)
        if ($windowEnd -lt $windowStart) { $windowEnd = $windowStart }
        $candidates = @()
        foreach ($historyEvent in @($History.Events)) {
            $document = $historyEvent.CloudTrailEvent | ConvertFrom-Json
            $documentText = $historyEvent.CloudTrailEvent
            $eventTime = ConvertFrom-IsoTimestamp -Value $historyEvent.EventTime
            $actorMatches = $document.userIdentity.arn -like "*/$($expected.ActorRoleName)/*" -or
                $document.userIdentity.sessionContext.sessionIssuer.userName -eq $expected.ActorRoleName
            $resourceMatches = $true
            foreach ($token in @($expected.ResourceTokens)) {
                if (-not $documentText.Contains($token, [StringComparison]::Ordinal)) {
                    $resourceMatches = $false
                    break
                }
            }
            $eventIdMatches = -not $expected.EventId -or
                $historyEvent.EventId -eq $expected.EventId
            $timeMatches = $expected.EventId -or
                ($eventTime -ge $windowStart -and $eventTime -le $windowEnd)
            if (-not $usedEventIds.Contains($historyEvent.EventId) -and $eventIdMatches -and
                $timeMatches -and $historyEvent.EventSource -eq $expected.EventSource -and
                $historyEvent.EventName -eq $expected.EventName -and $actorMatches -and
                [string]::IsNullOrWhiteSpace($document.errorCode) -and $resourceMatches) {
                $candidates += $historyEvent
            }
        }
        $candidates = @($candidates | Group-Object EventId | ForEach-Object {
            $_.Group[0]
        })
        $matched = if ($candidates.Count -eq 1) { $candidates[0] } else { $null }
        if (-not $matched) {
            $missingAttempts.Add($expected.AttemptId)
            continue
        }
        [void]$usedEventIds.Add($matched.EventId)
        $correlations.Add([ordered]@{
            Ts = $matched.EventTime
            Type = 'cloudtrail-final-history-correlation'
            EventSource = $matched.EventSource
            EventName = $matched.EventName
            EventId = $matched.EventId
            AttemptId = $expected.AttemptId
        })
    }

    [pscustomobject]@{
        MissingAttemptIds = @($missingAttempts)
        Correlations = @($correlations)
    }
}

if ($Phase -eq 'CollectEvidence') {
    Initialize-StateStorage
    $state = Read-State
    if ($state.AccountId -ne $ExpectedAccountId -or $state.RunId -ne $RunId) {
        throw 'O estado restrito não pertence à conta e ao RunId autorizados.'
    }
    if ($state.Status -notin @('exercised-awaiting-evidence', 'evidence-pending')) {
        throw "CollectEvidence não aceita o estado $($state.Status)."
    }

    $trailStatus = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
        'cloudtrail', 'get-trail-status', '--name', $state.TrailName,
        '--region', $Region, '--output', 'json'
    )
    if (-not [string]::IsNullOrWhiteSpace($trailStatus.LatestDeliveryError) -or
        -not [string]::IsNullOrWhiteSpace($trailStatus.LatestDigestDeliveryError)) {
        throw 'O CloudTrail reportou erro de entrega de log ou digest.'
    }
    $exerciseCompletedAt = ConvertFrom-IsoTimestamp -Value $state.ExerciseCompletedAt
    $lastDelivery = if ($trailStatus.LatestDeliveryTime) {
        ConvertFrom-IsoTimestamp -Value $trailStatus.LatestDeliveryTime
    }
    else {
        [System.DateTimeOffset]::MinValue
    }
    if ($lastDelivery -lt $exerciseCompletedAt) {
        $state.Status = 'evidence-pending'
        Save-State -State $state
        [pscustomobject]@{
            Status = $state.Status
            ExerciseCompletedAt = ConvertTo-CanonicalUtcTimestamp -Value $exerciseCompletedAt
            LatestDeliveryTime = if ($lastDelivery -eq [System.DateTimeOffset]::MinValue) {
                $null
            }
            else { ConvertTo-CanonicalUtcTimestamp -Value $lastDelivery }
        } | ConvertTo-Json -Compress
        exit 4
    }

    $corpus = Get-CloudTrailCorpus -Credential $operatorCredential -State $state
    if ($corpus.LogKeys.Count -eq 0 -or $corpus.DigestKeys.Count -eq 0 -or
        $corpus.Records.Count -eq 0) {
        $state.Status = 'evidence-pending'
        Save-State -State $state
        [pscustomobject]@{ Status = $state.Status; Reason = 'O corpus do CloudTrail ainda não contém logs e digests.' } | ConvertTo-Json -Compress
        exit 4
    }
    Assert-LocalCloudTrailManifest -State $state

    $commonText = if (Test-Path -LiteralPath $CommonLogPath) {
        Get-Content -Raw -LiteralPath $CommonLogPath
    }
    else { '' }
    $sentinels = @(
        $state.Sentinels.BodyA, $state.Sentinels.BodyB,
        $state.Sentinels.BodyABase64, $state.Sentinels.BodyBBase64,
        $state.Sentinels.OriginalName,
        $state.Sentinels.Sha256A, $state.Sentinels.Sha256B,
        $state.Sentinels.Sha256ABase64, $state.Sentinels.Sha256BBase64
    )
    $leaks = @()
    foreach ($sentinel in $sentinels) {
        if ($commonText.Contains($sentinel, [StringComparison]::Ordinal) -or
            $corpus.Text.Contains($sentinel, [StringComparison]::Ordinal)) {
            $leaks += $sentinel
        }
    }
    if ($leaks.Count -gt 0) {
        throw 'A varredura de vazamento encontrou sentinelas nos logs.'
    }

    $correlationResult = Get-CloudTrailCorrelationResult `
        -State $state -Records @($corpus.Records) `
        -ExpectedEvents @($state.ExpectedEvents)
    foreach ($correlation in @($correlationResult.Correlations)) {
        Write-RestrictedEvent -Entry $correlation
    }
    if ($correlationResult.MissingCallIds.Count -gt 0) {
        $state.Status = 'evidence-pending'
        Save-State -State $state
        [pscustomobject]@{
            Status = $state.Status
            MissingCallIds = @($correlationResult.MissingCallIds)
            ExpectedEvents = @($state.ExpectedEvents).Count
            CloudTrailEvents = $corpus.Records.Count
        } | ConvertTo-Json -Compress
        exit 4
    }

    Add-TestResult -State $state -Item 11 -Verdict 'PASS' `
        -Evidence 'Cada chamada foi correlacionada por ator, ação, recurso, resultado, janela e EventId; varredura sem sentinelas.'
    $state.Evidence.Collected = $true
    $state.Evidence.LeakScanPassed = $true
    $state.Evidence.CloudTrailEvents = $corpus.Records.Count
    $state.Evidence.CloudTrailLogObjects = $corpus.LogKeys.Count
    $state.Evidence.CloudTrailDigestObjects = $corpus.DigestKeys.Count
    $state.Evidence.Correlations = @($correlationResult.Correlations)
    $state.Status = 'evidence-collected-awaiting-cleanup'
    Save-State -State $state

    [pscustomobject]@{
        Status = $state.Status
        CloudTrailObjects = $corpus.Keys.Count
        CloudTrailEvents = $corpus.Records.Count
        LeakScanPassed = $true
        CorrelatedCalls = $correlationResult.Correlations.Count
    } | ConvertTo-Json -Compress
}

function Assert-BucketOwned {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string] $Bucket,
        [System.Collections.IDictionary] $State,
        [string] $CreateOperationId
    )

    $tagResult = Invoke-Aws -Credential $Credential -AllowFailure -Arguments @(
        's3api', 'get-bucket-tagging', '--bucket', $Bucket, '--output', 'json'
    )
    if ($tagResult.ExitCode -eq 0) {
        $tags = $tagResult.Output | ConvertFrom-Json
        if (Test-ExpectedTags -Tags $tags.TagSet -KeyName 'Key' -ValueName 'Value') {
            return
        }
    }
    if (-not $State -or [string]::IsNullOrWhiteSpace($CreateOperationId)) {
        throw "O bucket $Bucket não possui as tags de ownership esperadas."
    }
    $head = Invoke-ProfileAws -Arguments @(
        's3api', 'head-bucket', '--profile', $Profile, '--bucket', $Bucket,
        '--expected-bucket-owner', $ExpectedAccountId
    )
    $creationEvent = Find-MutationEvent -State $State -OperationId $CreateOperationId
    if ($head.ExitCode -ne 0 -or -not $creationEvent) {
        throw "O bucket $Bucket sem tags ainda não possui prova autenticada de criação e ownership."
    }
}

function Complete-MutationFromCloudTrailEvent {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][psobject] $Event,
        [ValidateSet('reconciled', 'not-applied')][string] $Status = 'reconciled'
    )

    $eventTime = ConvertTo-CanonicalUtcTimestamp -Value (
        ConvertFrom-IsoTimestamp -Value $Event.EventTime
    )
    $mutation = Get-Mutation -State $State -OperationId $OperationId
    $attempt = if ($Event.AraiaAttemptId) {
        @($mutation.Attempts | Where-Object AttemptId -eq $Event.AraiaAttemptId |
            Select-Object -First 1)[0]
    }
    else { $null }
    $observedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    $localCompletedAt = Get-MonotonicCompletionTimestamp `
        -StartedAt $(if ($attempt) { $attempt.StartedAt } else { $mutation.StartedAt }) `
        -CompletedAt $(if ($attempt) { $attempt.CompletedAt } else { $mutation.CompletedAt }) `
        -ObservedAt $observedAt
    if ($attempt) {
        $attempt.LocalOutcome = 'reconciled'
        $attempt.CompletedAt = $localCompletedAt
        $attempt.ExitCode = 0
        $attempt.ErrorCode = $null
        $attempt.EventTime = $eventTime
        $attempt.EventId = $Event.EventId
    }
    Complete-Mutation -State $State -OperationId $OperationId -Status $Status `
        -EventTime $eventTime -EventId $Event.EventId `
        -CompletedAt $localCompletedAt
}

function Resolve-AbsentProvisionMutation {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId
    )

    $successEvent = Find-MutationEvent -State $State -OperationId $OperationId
    if ($successEvent) { return 'successful-event-awaiting-resource' }

    Resolve-LocalCliValidationFailureAttempts -State $State -OperationId $OperationId
    if (Test-AllMutationAttemptsDefinitelyFailed -State $State -OperationId $OperationId) {
        Complete-Mutation -State $State -OperationId $OperationId -Status 'not-applied'
        return 'not-applied'
    }
    'indeterminate'
}

function Resolve-ProvisionResources {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $State)

    if ($State.Cleanup.ProvisionReconciled) { return @() }
    $indeterminate = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )

    foreach ($bucketEntry in @(
        [ordered]@{ Name = $State.TrailBucket; Flag = 'TrailBucket'; Operation = 'create-trail-bucket' },
        [ordered]@{ Name = $State.ObjectBucket; Flag = 'ObjectBucket'; Operation = 'create-object-bucket' }
    )) {
        $intent = Get-Mutation -State $State -OperationId $bucketEntry.Operation
        if (-not $intent -or $State.Created[$bucketEntry.Flag]) { continue }
        $head = Invoke-ProfileAws -Arguments @(
            's3api', 'head-bucket', '--profile', $Profile, '--bucket', $bucketEntry.Name,
            '--expected-bucket-owner', $ExpectedAccountId
        )
        if ($head.ExitCode -eq 0) {
            $event = Find-MutationEvent -State $State -OperationId $bucketEntry.Operation
            if (-not $event) {
                $null = $indeterminate.Add($bucketEntry.Operation)
                continue
            }
            $tagResult = Invoke-ProfileAws -Arguments @(
                's3api', 'get-bucket-tagging', '--profile', $Profile,
                '--bucket', $bucketEntry.Name, '--output', 'json'
            )
            if ($tagResult.ExitCode -ne 0 -and
                (Get-AwsErrorCode -Output $tagResult.Output) -notin @('NoSuchTagSet', 'NoSuchTagSetError')) {
                throw "Não foi possível validar as tags do bucket $($bucketEntry.Name): $($tagResult.Output)"
            }
            if ($tagResult.ExitCode -eq 0) {
                $tags = $tagResult.Output | ConvertFrom-Json
                if (-not (Test-ExpectedTags -Tags $tags.TagSet -KeyName 'Key' -ValueName 'Value')) {
                    throw "O bucket reconciliado $($bucketEntry.Name) possui tags de ownership divergentes."
                }
            }
            $ownershipResult = Invoke-ProfileAws -Arguments @(
                's3api', 'get-bucket-ownership-controls', '--profile', $Profile,
                '--bucket', $bucketEntry.Name, '--output', 'json'
            )
            if ($ownershipResult.ExitCode -ne 0) {
                throw "Não foi possível validar o ownership do bucket $($bucketEntry.Name): $($ownershipResult.Output)"
            }
            $ownership = $ownershipResult.Output | ConvertFrom-Json
            if (@($ownership.OwnershipControls.Rules).Count -ne 1 -or
                $ownership.OwnershipControls.Rules[0].ObjectOwnership -ne 'BucketOwnerEnforced') {
                throw "O bucket reconciliado $($bucketEntry.Name) não confirmou BucketOwnerEnforced."
            }
            if ($bucketEntry.Flag -eq 'ObjectBucket') {
                $lockResult = Invoke-ProfileAws -Arguments @(
                    's3api', 'get-object-lock-configuration', '--profile', $Profile,
                    '--bucket', $bucketEntry.Name, '--output', 'json'
                )
                if ($lockResult.ExitCode -ne 0 -or
                    ($lockResult.Output | ConvertFrom-Json).ObjectLockConfiguration.ObjectLockEnabled -ne 'Enabled') {
                    throw 'O bucket de objetos reconciliado não confirmou Object Lock habilitado.'
                }
            }
            $State.Created[$bucketEntry.Flag] = $true
            Complete-MutationFromCloudTrailEvent -State $State `
                -OperationId $bucketEntry.Operation -Event $event
        }
        elseif ((Get-AwsErrorCode -Output $head.Output) -in @('404', 'NoSuchBucket')) {
            if ((Resolve-AbsentProvisionMutation -State $State `
                -OperationId $bucketEntry.Operation) -ne 'not-applied') {
                $null = $indeterminate.Add($bucketEntry.Operation)
            }
        }
        else {
            throw "Não foi possível reconciliar o bucket $($bucketEntry.Name)."
        }
    }

    $trailIntent = Get-Mutation -State $State -OperationId 'create-trail'
    if ($trailIntent -and -not $State.Created.Trail) {
        $trailResult = Invoke-ProfileAws -Arguments @(
            'cloudtrail', 'get-trail', '--profile', $Profile, '--name', $State.TrailName,
            '--region', $Region, '--output', 'json'
        )
        if ($trailResult.ExitCode -eq 0) {
            $event = Find-MutationEvent -State $State -OperationId 'create-trail'
            if (-not $event) {
                $null = $indeterminate.Add('create-trail')
                return @($indeterminate)
            }
            $trail = $trailResult.Output | ConvertFrom-Json
            if ($trail.Trail.Name -ne $State.TrailName -or
                $trail.Trail.TrailARN -ne $State.TrailArn -or
                $trail.Trail.HomeRegion -ne $Region -or
                $trail.Trail.S3BucketName -ne $State.TrailBucket -or
                -not $trail.Trail.LogFileValidationEnabled) {
                throw 'O trail reconciliado divergiu dos atributos integrais esperados.'
            }
            $trailTagsResult = Invoke-ProfileAws -Arguments @(
                'cloudtrail', 'list-tags', '--profile', $Profile,
                '--resource-id-list', $State.TrailArn, '--region', $Region, '--output', 'json'
            )
            if ($trailTagsResult.ExitCode -ne 0) {
                throw "Não foi possível validar as tags do trail reconciliado: $($trailTagsResult.Output)"
            }
            $trailTags = $trailTagsResult.Output | ConvertFrom-Json
            if (-not (Test-ExpectedTags -Tags @($trailTags.ResourceTagList[0].TagsList) `
                -KeyName 'Key' -ValueName 'Value')) {
                throw 'O trail reconciliado não possui as tags de ownership esperadas.'
            }
            $State.Created.Trail = $true
            Complete-MutationFromCloudTrailEvent -State $State `
                -OperationId 'create-trail' -Event $event
        }
        elseif ((Get-AwsErrorCode -Output $trailResult.Output) -in @('TrailNotFoundException')) {
            if ((Resolve-AbsentProvisionMutation -State $State `
                -OperationId 'create-trail') -ne 'not-applied') {
                $null = $indeterminate.Add('create-trail')
            }
        }
        else {
            throw 'Não foi possível reconciliar o trail planejado.'
        }
    }
    if ($State.Created.Trail) {
        $trailStatusResult = Invoke-ProfileAws -Arguments @(
            'cloudtrail', 'get-trail-status', '--profile', $Profile,
            '--name', $State.TrailName, '--region', $Region, '--output', 'json'
        )
        if ($trailStatusResult.ExitCode -ne 0) {
            throw "Não foi possível reconciliar o estado de logging do trail: $($trailStatusResult.Output)"
        }
        $trailStatus = $trailStatusResult.Output | ConvertFrom-Json
        if ($State.Status -in @('provisioning', 'provision-failed', 'provisioned-not-verified')) {
            $State.Created.TrailLoggingStarted = [bool]$trailStatus.IsLogging
        }
        $startIntent = Get-Mutation -State $State -OperationId 'start-trail-logging'
        if ($trailStatus.IsLogging -and $startIntent -and $startIntent.Status -eq 'intent') {
            $event = Find-MutationEvent -State $State -OperationId 'start-trail-logging'
            if ($event) {
                Complete-MutationFromCloudTrailEvent -State $State `
                    -OperationId 'start-trail-logging' -Event $event
            }
        }
    }

    foreach ($roleName in $State.DataRoleNames) {
        $operationId = "create-data-role:$roleName"
        $intent = Get-Mutation -State $State -OperationId $operationId
        if (-not $intent -or $State.Created.RoleNames -contains $roleName) { continue }
        $roleResult = Invoke-ProfileAws -Arguments @(
            'iam', 'get-role', '--profile', $Profile, '--role-name', $roleName, '--output', 'json'
        )
        if ($roleResult.ExitCode -eq 0) {
            $event = Find-MutationEvent -State $State -OperationId $operationId
            if (-not $event) {
                $null = $indeterminate.Add($operationId)
                continue
            }
            $role = $roleResult.Output | ConvertFrom-Json
            $expectedTrust = [ordered]@{
                Version = '2012-10-17'
                Statement = @(
                    [ordered]@{
                        Effect = 'Allow'
                        Principal = [ordered]@{ AWS = $State.OperatorRoleArn }
                        Action = 'sts:AssumeRole'
                    }
                )
            }
            if (-not (Test-ExpectedTags -Tags $role.Role.Tags -KeyName 'Key' -ValueName 'Value') -or
                (ConvertTo-CanonicalJson -Value $role.Role.AssumeRolePolicyDocument) -ne
                    (ConvertTo-CanonicalJson -Value $expectedTrust) -or
                $role.Role.MaxSessionDuration -ne 3600 -or $role.Role.Path -ne '/') {
                throw "A role reconciliada $roleName divergiu dos atributos integrais esperados."
            }
            $State.Created.RoleNames = @($State.Created.RoleNames) + $roleName
            Complete-MutationFromCloudTrailEvent -State $State `
                -OperationId $operationId -Event $event
        }
        elseif ((Get-AwsErrorCode -Output $roleResult.Output) -eq 'NoSuchEntity') {
            if ((Resolve-AbsentProvisionMutation -State $State `
                -OperationId $operationId) -ne 'not-applied') {
                $null = $indeterminate.Add($operationId)
            }
        }
        else {
            throw "Não foi possível reconciliar a role $roleName."
        }
    }

    foreach ($keyPlan in @(
        [ordered]@{ Slot = 'A'; Operation = 'create-key-a'; Description = "$Prefix chave A" },
        [ordered]@{ Slot = 'B'; Operation = 'create-key-b'; Description = "$Prefix chave B" }
    )) {
        $intent = Get-Mutation -State $State -OperationId $keyPlan.Operation
        $arnProperty = "Key$($keyPlan.Slot)Arn"
        $idProperty = "Key$($keyPlan.Slot)Id"
        if (-not $intent -or $State[$arnProperty]) { continue }
        $event = Find-MutationEvent -State $State -OperationId $keyPlan.Operation
        if (-not $event) {
            if ((Resolve-AbsentProvisionMutation -State $State `
                -OperationId $keyPlan.Operation) -ne 'not-applied') {
                $null = $indeterminate.Add($keyPlan.Operation)
            }
            continue
        }
        $eventDocument = $event.CloudTrailEvent | ConvertFrom-Json
        $eventKeyId = $eventDocument.responseElements.keyMetadata.keyId
        $eventKeyArn = $eventDocument.responseElements.keyMetadata.arn
        if ([string]::IsNullOrWhiteSpace($eventKeyId) -or
            [string]::IsNullOrWhiteSpace($eventKeyArn)) {
            throw "O evento de criação da CMK $($keyPlan.Slot) não contém KeyId e ARN."
        }
        $metadata = $null
        for ($attempt = 1; $attempt -le 6; $attempt++) {
            $descriptionResult = Invoke-ProfileAws -Arguments @(
                'kms', 'describe-key', '--profile', $Profile, '--key-id', $eventKeyId,
                '--region', $Region, '--output', 'json'
            )
            $tagResult = Invoke-ProfileAws -Arguments @(
                'kms', 'list-resource-tags', '--profile', $Profile, '--key-id', $eventKeyId,
                '--region', $Region, '--output', 'json'
            )
            if ($descriptionResult.ExitCode -eq 0 -and $tagResult.ExitCode -eq 0) {
                $description = $descriptionResult.Output | ConvertFrom-Json
                $tags = $tagResult.Output | ConvertFrom-Json
                if ($description.KeyMetadata.KeyId -ne $eventKeyId -or
                    $description.KeyMetadata.Arn -ne $eventKeyArn -or
                    $description.KeyMetadata.Description -ne $keyPlan.Description -or
                    $description.KeyMetadata.KeyManager -ne 'CUSTOMER' -or
                    $description.KeyMetadata.Origin -ne 'AWS_KMS' -or
                    $description.KeyMetadata.KeyUsage -ne 'ENCRYPT_DECRYPT' -or
                    $description.KeyMetadata.KeySpec -ne 'SYMMETRIC_DEFAULT' -or
                    -not (Test-ExpectedTags -Tags $tags.Tags `
                        -KeyName 'TagKey' -ValueName 'TagValue')) {
                    throw "A CMK reconciliada $($keyPlan.Slot) divergiu dos atributos integrais esperados."
                }
                $metadata = $description.KeyMetadata
                break
            }
            $descriptionError = Get-AwsErrorCode -Output $descriptionResult.Output
            $tagError = Get-AwsErrorCode -Output $tagResult.Output
            $transientErrors = @('NotFoundException', 'KMSInvalidStateException')
            if (($descriptionResult.ExitCode -ne 0 -and $descriptionError -notin $transientErrors) -or
                ($tagResult.ExitCode -ne 0 -and $tagError -notin $transientErrors)) {
                throw "A leitura da CMK reconciliada falhou: $($descriptionResult.Output) $($tagResult.Output)"
            }
            if ($attempt -lt 6) { Start-Sleep -Seconds 5 }
        }
        if (-not $metadata) {
            $null = $indeterminate.Add($keyPlan.Operation)
            continue
        }
        $State[$arnProperty] = $metadata.Arn
        $State[$idProperty] = $metadata.KeyId
        $State.Created.KeyIds = @($State.Created.KeyIds) + $metadata.KeyId
        Complete-MutationFromCloudTrailEvent -State $State `
            -OperationId $keyPlan.Operation -Event $event
    }

    $aliasesResult = Invoke-ProfileAws -Arguments @(
        'kms', 'list-aliases', '--profile', $Profile, '--region', $Region, '--output', 'json'
    )
    if ($aliasesResult.ExitCode -ne 0) {
        throw "Não foi possível inventariar os aliases para reconciliação: $($aliasesResult.Output)"
    }
    $aliases = ($aliasesResult.Output | ConvertFrom-Json).Aliases
    foreach ($aliasPlan in @(
        [ordered]@{ Name = $State.KeyAAlias; Operation = 'create-alias-a'; Target = $State.KeyAId },
        [ordered]@{ Name = $State.KeyBAlias; Operation = 'create-alias-b'; Target = $State.KeyBId }
    )) {
        $intent = Get-Mutation -State $State -OperationId $aliasPlan.Operation
        if (-not $intent -or $State.Created.AliasNames -contains $aliasPlan.Name) { continue }
        $event = Find-MutationEvent -State $State -OperationId $aliasPlan.Operation
        $alias = @($aliases | Where-Object AliasName -eq $aliasPlan.Name |
            Select-Object -First 1)[0]
        if ($alias) {
            if (-not $event) {
                $null = $indeterminate.Add($aliasPlan.Operation)
                continue
            }
            $eventDocument = $event.CloudTrailEvent | ConvertFrom-Json
            if ([string]::IsNullOrWhiteSpace($aliasPlan.Target) -or
                $alias.TargetKeyId -ne $aliasPlan.Target -or
                $eventDocument.requestParameters.targetKeyId -ne $aliasPlan.Target) {
                throw "O alias reconciliado $($aliasPlan.Name) aponta para uma CMK inesperada."
            }
            $State.Created.AliasNames = @($State.Created.AliasNames) + $aliasPlan.Name
            Complete-MutationFromCloudTrailEvent -State $State `
                -OperationId $aliasPlan.Operation -Event $event
        }
        elseif ((Resolve-AbsentProvisionMutation -State $State `
            -OperationId $aliasPlan.Operation) -ne 'not-applied') {
            $null = $indeterminate.Add($aliasPlan.Operation)
        }
    }

    $creationOperations = @(
        'create-trail-bucket', 'create-object-bucket', 'create-trail',
        'create-key-a', 'create-key-b', 'create-alias-a', 'create-alias-b'
    ) + @($State.DataRoleNames | ForEach-Object { "create-data-role:$_" })
    foreach ($mutation in @($State.Mutations | Where-Object Status -eq 'intent')) {
        if ($mutation.OperationId -in $creationOperations -or
            $mutation.OperationId.StartsWith('matrix:', [StringComparison]::Ordinal) -or
            $mutation.OperationId.StartsWith('final-', [StringComparison]::Ordinal)) {
            continue
        }
        $successEvent = Find-MutationEvent -State $State -OperationId $mutation.OperationId
        if ($successEvent) {
            Complete-MutationFromCloudTrailEvent -State $State `
                -OperationId $mutation.OperationId -Event $successEvent
            continue
        }
        Resolve-LocalCliValidationFailureAttempts -State $State `
            -OperationId $mutation.OperationId
        if (Test-AllMutationAttemptsDefinitelyFailed -State $State `
            -OperationId $mutation.OperationId) {
            Complete-Mutation -State $State -OperationId $mutation.OperationId `
                -Status 'not-applied'
            continue
        }
        $null = $indeterminate.Add($mutation.OperationId)
    }
    foreach ($mutation in @($State.Mutations | Where-Object {
        $_.Status -eq 'intent' -and $_.OperationId -in $creationOperations
    })) {
        $null = $indeterminate.Add($mutation.OperationId)
    }
    $State.Cleanup.IndeterminateProvisionOperations = @($indeterminate | Sort-Object)
    $State.Cleanup.ProvisionReconciled = $indeterminate.Count -eq 0
    Save-State -State $State
    @($indeterminate | Sort-Object)
}

function Assert-RoleOwned {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string] $RoleName
    )

    $role = Invoke-AwsJson -Credential $Credential -Arguments @(
        'iam', 'get-role', '--role-name', $RoleName, '--output', 'json'
    )
    if (-not (Test-ExpectedTags -Tags $role.Role.Tags -KeyName 'Key' -ValueName 'Value')) {
        throw "A role $RoleName não possui as tags de ownership esperadas."
    }
}

function Assert-KeyOwned {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string] $KeyArn
    )

    $tags = Invoke-AwsJson -Credential $Credential -Arguments @(
        'kms', 'list-resource-tags', '--key-id', $KeyArn, '--region', $Region, '--output', 'json'
    )
    if (-not (Test-ExpectedTags -Tags $tags.Tags -KeyName 'TagKey' -ValueName 'TagValue')) {
        throw "A CMK $KeyArn não possui as tags de ownership esperadas."
    }
}

function Assert-TrailOwned {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string] $TrailArn
    )

    $tags = Invoke-AwsJson -Credential $Credential -Arguments @(
        'cloudtrail', 'list-tags', '--resource-id-list', $TrailArn,
        '--region', $Region, '--output', 'json'
    )
    $trailTags = @($tags.ResourceTagList[0].TagsList)
    if (-not (Test-ExpectedTags -Tags $trailTags -KeyName 'Key' -ValueName 'Value')) {
        throw "O trail $TrailArn não possui as tags de ownership esperadas."
    }
}

function Remove-UnversionedBucketObjects {
    param(
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string] $Bucket
    )

    $listing = Invoke-AwsJson -Credential $Credential -Arguments @(
        's3api', 'list-objects-v2', '--bucket', $Bucket, '--output', 'json'
    )
    $contents = @($listing.Contents | Where-Object { $null -ne $_ })
    foreach ($item in $contents) {
        $keyHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($item.Key))
        ).ToLowerInvariant()
        $operationId = "final-delete-trail-object:$keyHash"
        $intent = Start-MutationIntent -State $script:ActiveState -OperationId $operationId `
            -ActorRoleName $script:ActiveState.OperatorRoleName `
            -EventSource 's3.amazonaws.com' -EventName 'DeleteObject' `
            -ResourceTokens @($Bucket, $item.Key)
        if ($intent.Status -in @('succeeded', 'reconciled')) { continue }
        $attempt = Start-MutationAttempt -State $script:ActiveState -OperationId $operationId
        $result = Invoke-AwsSingleAttempt -Credential $Credential -AllowFailure -Arguments @(
            's3api', 'delete-object', '--bucket', $Bucket, '--key', $item.Key
        )
        $errorCode = Get-AwsErrorCode -Output $result.Output
        $attemptOutcome = if ($result.ExitCode -eq 0) {
            'succeeded'
        }
        else {
            Get-FailureDisposition -Output $result.Output -ExitCode $result.ExitCode
        }
        Complete-MutationAttempt -State $script:ActiveState -OperationId $operationId `
            -AttemptId $attempt.AttemptId -LocalOutcome $attemptOutcome `
            -ExitCode $result.ExitCode -ErrorCode $errorCode
        if ($result.ExitCode -ne 0) {
            throw "A exclusão de um objeto do bucket de auditoria falhou: $($result.Output)"
        }
        Complete-Mutation -State $script:ActiveState -OperationId $operationId
    }
    foreach ($mutation in @($script:ActiveState.Mutations | Where-Object {
        $_.OperationId -like 'final-delete-trail-object:*' -and $_.Status -eq 'intent'
    })) {
        $key = @($mutation.ResourceTokens)[1]
        $wasAuthenticated = @($script:ActiveState.Evidence.CloudTrailManifest |
            Where-Object Key -eq $key).Count -eq 1
        if ($wasAuthenticated -and $contents.Key -notcontains $key) {
            $latestAttempt = @($mutation.Attempts | Where-Object LocalOutcome -eq 'in-flight' |
                Sort-Object Sequence -Descending | Select-Object -First 1)[0]
            if ($latestAttempt) {
                Complete-MutationAttempt -State $script:ActiveState `
                    -OperationId $mutation.OperationId -AttemptId $latestAttempt.AttemptId `
                    -LocalOutcome 'reconciled' -ExitCode $null -ErrorCode $null
            }
            Complete-Mutation -State $script:ActiveState `
                -OperationId $mutation.OperationId -Status 'reconciled'
        }
    }
}

function Add-FinalAuditExpectation {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][string] $AttemptId,
        [Parameter(Mandatory = $true)][string] $ActorRoleName,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string[]] $ResourceTokens,
        [Parameter(Mandatory = $true)][string] $StartedAt,
        [AllowNull()][string] $CompletedAt,
        [AllowNull()][string] $EventTime,
        [AllowNull()][string] $EventId,
        [ValidateSet('intent', 'completed', 'failed')][string] $Status = 'intent'
    )

    $eventSource = switch ($Arguments[0]) {
        's3api' { 's3.amazonaws.com' }
        'iam' { 'iam.amazonaws.com' }
        'cloudtrail' { 'cloudtrail.amazonaws.com' }
        default { throw "Serviço final não mapeado: $($Arguments[0])" }
    }
    $eventName = Get-CloudTrailEventName -Operation $Arguments[1]
    $existing = @($State.Cleanup.ExpectedFinalEvents |
        Where-Object AttemptId -eq $AttemptId | Select-Object -First 1)[0]
    if ($existing) {
        if ($existing.OperationId -ne $OperationId -or
            $existing.EventSource -ne $eventSource -or $existing.EventName -ne $eventName -or
            $existing.ActorRoleName -ne $ActorRoleName -or
            (ConvertTo-CanonicalJson -Value @($existing.ResourceTokens)) -ne
                (ConvertTo-CanonicalJson -Value @($ResourceTokens))) {
            throw "A expectativa final $AttemptId colidiu com outra mutação."
        }
        if ($CompletedAt) {
            $existing.CompletedAt = $CompletedAt
        }
        if ($EventTime) { $existing.EventTime = $EventTime }
        if ($EventId) { $existing.EventId = $EventId }
        $existing.Status = $Status
        Save-State -State $State
        return $existing
    }
    $expectation = [ordered]@{
        OperationId = $OperationId
        AttemptId = $AttemptId
        EventSource = $eventSource
        EventName = $eventName
        ActorRoleName = $ActorRoleName
        ResourceTokens = $ResourceTokens
        StartedAt = $StartedAt
        CompletedAt = $CompletedAt
        EventTime = $EventTime
        EventId = $EventId
        Status = $Status
    }
    $State.Cleanup.ExpectedFinalEvents = @($State.Cleanup.ExpectedFinalEvents) + $expectation
    Save-State -State $State
    $expectation
}

function Invoke-FinalOperatorMutation {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][psobject] $Credential,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string[]] $ResourceTokens
    )

    $eventSource = switch ($Arguments[0]) {
        's3api' { 's3.amazonaws.com' }
        'iam' { 'iam.amazonaws.com' }
        'cloudtrail' { 'cloudtrail.amazonaws.com' }
        default { throw "Serviço final não mapeado: $($Arguments[0])" }
    }
    Start-MutationIntent -State $State -OperationId $OperationId `
        -ActorRoleName $State.OperatorRoleName -EventSource $eventSource `
        -EventName (Get-CloudTrailEventName -Operation $Arguments[1]) `
        -ResourceTokens $ResourceTokens | Out-Null
    $attempt = Start-MutationAttempt -State $State -OperationId $OperationId
    $startedAt = $attempt.StartedAt
    Add-FinalAuditExpectation -State $State -OperationId $OperationId `
        -AttemptId $attempt.AttemptId `
        -ActorRoleName $State.OperatorRoleName -Arguments $Arguments `
        -ResourceTokens $ResourceTokens -StartedAt $startedAt -CompletedAt $null `
        -Status 'intent' | Out-Null
    $result = Invoke-AwsSingleAttempt -Credential $Credential -AllowFailure -Arguments $Arguments
    $completedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    if ($result.ExitCode -ne 0) {
        $errorCode = Get-AwsErrorCode -Output $result.Output
        Complete-MutationAttempt -State $State -OperationId $OperationId `
            -AttemptId $attempt.AttemptId `
            -LocalOutcome (Get-FailureDisposition -Output $result.Output `
                -ExitCode $result.ExitCode) `
            -ExitCode $result.ExitCode -ErrorCode $errorCode -CompletedAt $completedAt
        Add-FinalAuditExpectation -State $State -OperationId $OperationId `
            -AttemptId $attempt.AttemptId -ActorRoleName $State.OperatorRoleName `
            -Arguments $Arguments -ResourceTokens $ResourceTokens `
            -StartedAt $startedAt -CompletedAt $completedAt -Status 'failed' | Out-Null
        throw "A mutação operacional final $OperationId falhou: $($result.Output)"
    }
    Invoke-FinalizationFault -OperationId $OperationId
    Complete-MutationAttempt -State $State -OperationId $OperationId `
        -AttemptId $attempt.AttemptId -LocalOutcome 'succeeded' `
        -ExitCode $result.ExitCode -ErrorCode $null -CompletedAt $completedAt
    Complete-Mutation -State $State -OperationId $OperationId -CompletedAt $completedAt
    Add-FinalAuditExpectation -State $State -OperationId $OperationId `
        -AttemptId $attempt.AttemptId `
        -ActorRoleName $State.OperatorRoleName `
        -Arguments $Arguments -ResourceTokens $ResourceTokens `
        -StartedAt $startedAt -CompletedAt $completedAt -Status 'completed' | Out-Null
}

function Invoke-FinalProfileMutation {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string[]] $ResourceTokens
    )

    $eventSource = switch ($Arguments[0]) {
        's3api' { 's3.amazonaws.com' }
        'iam' { 'iam.amazonaws.com' }
        'cloudtrail' { 'cloudtrail.amazonaws.com' }
        default { throw "Serviço final não mapeado: $($Arguments[0])" }
    }
    Start-MutationIntent -State $State -OperationId $OperationId `
        -ActorRoleName $adminRoleName -EventSource $eventSource `
        -EventName (Get-CloudTrailEventName -Operation $Arguments[1]) `
        -ResourceTokens $ResourceTokens | Out-Null
    $attempt = Start-MutationAttempt -State $State -OperationId $OperationId
    $startedAt = $attempt.StartedAt
    Add-FinalAuditExpectation -State $State -OperationId $OperationId `
        -AttemptId $attempt.AttemptId `
        -ActorRoleName $adminRoleName -Arguments $Arguments `
        -ResourceTokens $ResourceTokens -StartedAt $startedAt -CompletedAt $null `
        -Status 'intent' | Out-Null
    $result = Invoke-ProfileAwsSingleAttempt -Arguments $Arguments
    $completedAt = ConvertTo-CanonicalUtcTimestamp -Value ([System.DateTimeOffset]::UtcNow)
    if ($result.ExitCode -ne 0) {
        $errorCode = Get-AwsErrorCode -Output $result.Output
        Complete-MutationAttempt -State $State -OperationId $OperationId `
            -AttemptId $attempt.AttemptId `
            -LocalOutcome (Get-FailureDisposition -Output $result.Output `
                -ExitCode $result.ExitCode) `
            -ExitCode $result.ExitCode -ErrorCode $errorCode `
            -CompletedAt $completedAt
        Add-FinalAuditExpectation -State $State -OperationId $OperationId `
            -AttemptId $attempt.AttemptId -ActorRoleName $adminRoleName `
            -Arguments $Arguments -ResourceTokens $ResourceTokens `
            -StartedAt $startedAt -CompletedAt $completedAt -Status 'failed' | Out-Null
        throw "A mutação administrativa final falhou: $($result.Output)"
    }
    Invoke-FinalizationFault -OperationId $OperationId
    Complete-MutationAttempt -State $State -OperationId $OperationId `
        -AttemptId $attempt.AttemptId -LocalOutcome 'succeeded' `
        -ExitCode $result.ExitCode -ErrorCode $null -CompletedAt $completedAt
    Complete-Mutation -State $State -OperationId $OperationId -CompletedAt $completedAt
    Add-FinalAuditExpectation -State $State -OperationId $OperationId `
        -AttemptId $attempt.AttemptId `
        -ActorRoleName $adminRoleName `
        -Arguments $Arguments -ResourceTokens $ResourceTokens `
        -StartedAt $startedAt -CompletedAt $completedAt -Status 'completed' | Out-Null
}

function Confirm-MutationFromHistory {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][string] $OperationId
    )

    $mutation = Get-Mutation -State $State -OperationId $OperationId
    if (-not $mutation) { return $false }
    $event = Find-MutationEvent -State $State -OperationId $OperationId
    if (-not $event) { return $false }
    $eventTime = ConvertTo-CanonicalUtcTimestamp -Value (
        ConvertFrom-IsoTimestamp -Value $event.EventTime
    )
    Complete-MutationFromCloudTrailEvent -State $State -OperationId $OperationId `
        -Event $event -Status 'reconciled'
    if ($OperationId.StartsWith('matrix:', [StringComparison]::Ordinal)) {
        $callId = $OperationId.Substring('matrix:'.Length)
        $expected = @($State.ExpectedEvents | Where-Object CallId -eq $callId |
            Select-Object -First 1)[0]
        if ($expected) {
            $matchedAttempt = @($mutation.Attempts |
                Where-Object AttemptId -eq $event.AraiaAttemptId | Select-Object -First 1)[0]
            if ($matchedAttempt) {
                $expected.StartedAt = $matchedAttempt.StartedAt
                $expected.CompletedAt = $matchedAttempt.CompletedAt
            }
            $expected.AttemptId = $event.AraiaAttemptId
            $expected.EventTime = $eventTime
            $expected.EventId = $event.EventId
            $expected.ExitCode = 0
            $expected.ErrorCode = $null
            $expected.Status = 'completed'
        }
    }
    $finalExpected = @($State.Cleanup.ExpectedFinalEvents |
        Where-Object AttemptId -eq $event.AraiaAttemptId | Select-Object -First 1)[0]
    if ($finalExpected) {
        $matchedAttempt = @($mutation.Attempts |
            Where-Object AttemptId -eq $event.AraiaAttemptId | Select-Object -First 1)[0]
        if ($matchedAttempt) {
            $finalExpected.StartedAt = $matchedAttempt.StartedAt
            $finalExpected.CompletedAt = $matchedAttempt.CompletedAt
        }
        $finalExpected.EventTime = $eventTime
        $finalExpected.EventId = $event.EventId
        $finalExpected.Status = 'completed'
    }
    Save-State -State $State
    $true
}

function Complete-CleanupMutationsFromCorrelations {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][object[]] $Correlations
    )

    foreach ($correlation in $Correlations) {
        $expected = @($State.ExpectedEvents | Where-Object CallId -eq $correlation.CallId |
            Select-Object -First 1)[0]
        if (-not $expected) {
            throw "A correlação $($correlation.CallId) não possui expectativa autenticada."
        }
        if ($expected.EventId -and $expected.EventId -ne $correlation.EventId) {
            throw "A correlação $($correlation.CallId) conflita com o EventId autenticado."
        }
        if ($expected.EventId -and $expected.AttemptId -and $correlation.AttemptId -and
            $expected.AttemptId -ne $correlation.AttemptId) {
            throw "A correlação $($correlation.CallId) conflita com o AttemptId autenticado."
        }
        $operationId = "matrix:$($correlation.CallId)"
        $mutation = Get-Mutation -State $State -OperationId $operationId
        if (-not $mutation) {
            throw "A correlação $($correlation.CallId) não possui mutação autenticada."
        }
        $eventTimeOffset = ConvertFrom-IsoTimestamp -Value $correlation.Ts
        $attemptId = if ($correlation.AttemptId) {
            $correlation.AttemptId
        }
        else { $expected.AttemptId }
        $attempt = if ($attemptId) {
            @($mutation.Attempts | Where-Object AttemptId -eq $attemptId |
                Select-Object -First 1)[0]
        }
        else {
            Get-CompatibleMutationAttempt -Attempts @($mutation.Attempts) `
                -EventTime $eventTimeOffset `
                -Outcome $(if ($expected.ExpectSuccess) { 'success' } else { 'failure' })
        }
        if (-not $attempt) {
            throw "A correlação $($correlation.CallId) não possui tentativa compatível."
        }
        if ($attempt.EventId -and $attempt.EventId -ne $correlation.EventId) {
            throw "A tentativa $($attempt.AttemptId) conflita com o EventId correlacionado."
        }
        $outcome = if ($expected.ExpectSuccess) { 'success' } else { 'failure' }
        $compatibleAttempt = Get-CompatibleMutationAttempt -Attempts @($mutation.Attempts) `
            -EventTime $eventTimeOffset -Outcome $outcome
        if (-not $expected.EventId -and
            (-not $compatibleAttempt -or $compatibleAttempt.AttemptId -ne $attempt.AttemptId)) {
            throw "A tentativa $($attempt.AttemptId) não possui correlação causal única."
        }
        if (($expected.ExpectSuccess -and $attempt.LocalOutcome -eq 'failed-definitive') -or
            (-not $expected.ExpectSuccess -and $attempt.LocalOutcome -ne 'failed-definitive')) {
            throw "A tentativa $($attempt.AttemptId) possui resultado incompatível com a correlação."
        }
        $event = [pscustomobject]@{
            EventTime = $correlation.Ts
            EventId = $correlation.EventId
            AraiaAttemptId = $attempt.AttemptId
        }
        if ($expected.ExpectSuccess -and $mutation.Status -eq 'intent') {
            Complete-MutationFromCloudTrailEvent -State $State -OperationId $operationId `
                -Event $event -Status 'reconciled'
        }
        elseif ($expected.ExpectSuccess -and $mutation.Status -eq 'not-applied') {
            throw "A correlação de sucesso $($correlation.CallId) contradiz a mutação not-applied."
        }
        else {
            $attempt.EventTime = ConvertTo-CanonicalUtcTimestamp -Value $eventTimeOffset
            $attempt.EventId = $correlation.EventId
            $mutation.EventTime = $attempt.EventTime
            $mutation.EventId = $attempt.EventId
        }
        $expected.AttemptId = $attempt.AttemptId
        $expected.StartedAt = $attempt.StartedAt
        $expected.CompletedAt = $attempt.CompletedAt
        $expected.EventTime = ConvertTo-CanonicalUtcTimestamp -Value $eventTimeOffset
        $expected.EventId = $correlation.EventId
        $expected.Status = 'completed'
        Save-State -State $State
    }
}

function Complete-FinalAuditExpectationsFromCorrelations {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [Parameter(Mandatory = $true)][object[]] $Correlations
    )

    foreach ($correlation in $Correlations) {
        $expected = @($State.Cleanup.ExpectedFinalEvents |
            Where-Object AttemptId -eq $correlation.AttemptId | Select-Object -First 1)[0]
        if (-not $expected) {
            throw "A correlação final $($correlation.AttemptId) não possui expectativa autenticada."
        }
        if ($expected.EventId -and $expected.EventId -ne $correlation.EventId) {
            throw "A correlação final $($correlation.AttemptId) conflita com o EventId autenticado."
        }
        $mutation = Get-Mutation -State $State -OperationId $expected.OperationId
        if (-not $mutation) {
            throw "A correlação final $($correlation.AttemptId) não possui mutação autenticada."
        }
        $attempt = @($mutation.Attempts |
            Where-Object AttemptId -eq $expected.AttemptId | Select-Object -First 1)[0]
        if (-not $attempt) {
            throw "A correlação final $($correlation.AttemptId) não possui tentativa autenticada."
        }
        if ($attempt.EventId -and $attempt.EventId -ne $correlation.EventId) {
            throw "A tentativa final $($attempt.AttemptId) conflita com o EventId correlacionado."
        }
        $eventTime = ConvertTo-CanonicalUtcTimestamp -Value (
            ConvertFrom-IsoTimestamp -Value $correlation.Ts
        )
        $attempt.EventTime = $eventTime
        $attempt.EventId = $correlation.EventId
        $expected.EventTime = $eventTime
        $expected.EventId = $correlation.EventId
        $expected.Status = 'completed'
    }
    Save-State -State $State
}

function Get-IndeterminateMutationIds {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $State)

    @($State.Mutations | Where-Object {
        $_.Status -eq 'intent' -and
            -not $_.OperationId.StartsWith('final-', [StringComparison]::Ordinal)
    } | ForEach-Object OperationId | Sort-Object -Unique)
}

function Test-ExpectedEventAvailableInEventHistory {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Expected)

    switch ($Expected.EventSource) {
        'cloudtrail.amazonaws.com' { return $true }
        'iam.amazonaws.com' { return $true }
        'kms.amazonaws.com' { return $true }
        's3.amazonaws.com' { return $Expected.EventName -eq 'DeleteBucket' }
        default { return $false }
    }
}

function Resolve-ManagementCleanupMutationIntents {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [switch] $StopLoggingOnly
    )

    $eligibleExpected = @($State.ExpectedEvents | Where-Object {
        $_.Item -eq '12' -and $_.ExpectSuccess -and -not $_.EventId -and
            $(if ($StopLoggingOnly) {
                $_.Action -eq 'CleanupStopLogging'
            }
            else {
                Test-ExpectedEventAvailableInEventHistory -Expected $_
            })
    })
    $successfulCleanupOperationIds = @($eligibleExpected |
        ForEach-Object { "matrix:$($_.CallId)" })
    foreach ($mutation in @($State.Mutations | Where-Object {
        $_.OperationId.StartsWith('matrix:', [StringComparison]::Ordinal) -and
            $_.OperationId -in $successfulCleanupOperationIds -and
            ($_.Status -eq 'intent' -or
                ($_.Status -in @('succeeded', 'reconciled') -and
                    $_.OperationId -in $successfulCleanupOperationIds))
    })) {
        Confirm-MutationFromHistory -State $State `
            -OperationId $mutation.OperationId | Out-Null
    }
    $unprovenSuccesses = @($eligibleExpected | Where-Object { -not $_.EventId } |
        ForEach-Object { "matrix:$($_.CallId)" })
    if ($StopLoggingOnly) {
        return @($unprovenSuccesses | Sort-Object -Unique)
    }
    @(@(Get-IndeterminateMutationIds -State $State) + $unprovenSuccesses |
        Sort-Object -Unique)
}

function Complete-CleanupFinalization {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [AllowNull()][psobject] $Credential,
        [int] $FinalCorpusEvents = 0
    )

    if ($State.Created.Trail) {
        $trailCheck = Invoke-ProfileAws -Arguments @(
            'cloudtrail', 'get-trail', '--profile', $Profile, '--name', $State.TrailName,
            '--region', $Region, '--output', 'json'
        )
        if ($trailCheck.ExitCode -eq 0) {
            if (-not $Credential) { throw 'A credencial do operador está ausente antes da exclusão do trail.' }
            Assert-TrailOwned -Credential $Credential -TrailArn $State.TrailArn
            Invoke-FinalOperatorMutation -State $State -OperationId 'final-delete-trail' `
                -Credential $Credential `
                -Arguments @(
                    'cloudtrail', 'delete-trail', '--name', $State.TrailName, '--region', $Region
                ) -ResourceTokens @($State.TrailName)
        }
        elseif ((Get-AwsErrorCode -Output $trailCheck.Output) -notin @('TrailNotFoundException') -or
            -not (Confirm-MutationFromHistory -State $State -OperationId 'final-delete-trail')) {
            throw 'A ausência do trail não pôde ser reconciliada com uma mutação autenticada.'
        }
        Write-Journal -Type 'delete' -ResourceType 'cloudtrail-trail' `
            -ResourceId $State.TrailArn -Status 'succeeded'
        $State.Created.Trail = $false
        $State.Created.TrailLoggingStarted = $false
        Save-State -State $State
    }

    if ($State.Created.TrailBucket) {
        $trailBucketCheck = Invoke-ProfileAws -Arguments @(
            's3api', 'head-bucket', '--profile', $Profile, '--bucket', $State.TrailBucket,
            '--expected-bucket-owner', $ExpectedAccountId
        )
        if ($trailBucketCheck.ExitCode -eq 0) {
            if (-not $Credential) { throw 'A credencial do operador está ausente antes da exclusão do bucket de auditoria.' }
            Assert-BucketOwned -Credential $Credential -Bucket $State.TrailBucket `
                -State $State -CreateOperationId 'create-trail-bucket'
            Remove-UnversionedBucketObjects -Credential $Credential -Bucket $State.TrailBucket
            Invoke-FinalOperatorMutation -State $State -OperationId 'final-delete-trail-bucket' `
                -Credential $Credential `
                -Arguments @(
                    's3api', 'delete-bucket', '--bucket', $State.TrailBucket, '--region', $Region
                ) -ResourceTokens @($State.TrailBucket)
        }
        elseif ((Get-AwsErrorCode -Output $trailBucketCheck.Output) -notin @('404', 'NoSuchBucket') -or
            -not (Confirm-MutationFromHistory -State $State `
                -OperationId 'final-delete-trail-bucket')) {
            throw 'A ausência do bucket de auditoria não pôde ser reconciliada.'
        }
        Write-Journal -Type 'delete' -ResourceType 's3-bucket' `
            -ResourceId $State.TrailBucket -Status 'succeeded'
        $State.Created.TrailBucket = $false
        Save-State -State $State
    }

    if ($State.Created.OperatorRole) {
        $operatorCheck = Invoke-ProfileAws -Arguments @(
            'iam', 'get-role', '--profile', $Profile, '--role-name', $OperatorRoleName, '--output', 'json'
        )
        if ($operatorCheck.ExitCode -eq 0) {
            $operatorForDelete = $operatorCheck.Output | ConvertFrom-Json
            if (-not (Test-ExpectedTags -Tags $operatorForDelete.Role.Tags `
                -KeyName 'Key' -ValueName 'Value')) {
                throw 'A role temporária não passou pela verificação de ownership final.'
            }
            $policyListing = Invoke-ProfileAws -Arguments @(
                'iam', 'list-role-policies', '--profile', $Profile,
                '--role-name', $OperatorRoleName, '--output', 'json'
            )
            if ($policyListing.ExitCode -ne 0) {
                throw "Não foi possível listar a policy final: $($policyListing.Output)"
            }
            $policyNames = @((($policyListing.Output | ConvertFrom-Json).PolicyNames))
            if ($policyNames -contains 'ExperimentOperatorPolicy') {
                Invoke-FinalProfileMutation -State $State `
                    -OperationId 'final-delete-operator-policy' -Arguments @(
                    'iam', 'delete-role-policy', '--profile', $Profile,
                    '--role-name', $OperatorRoleName, '--policy-name', 'ExperimentOperatorPolicy'
                ) -ResourceTokens @($OperatorRoleName, 'ExperimentOperatorPolicy')
            }
            elseif (-not (Confirm-MutationFromHistory -State $State `
                -OperationId 'final-delete-operator-policy')) {
                throw 'A ausência da policy temporária não pôde ser reconciliada.'
            }
            Invoke-FinalProfileMutation -State $State `
                -OperationId 'final-delete-operator-role' -Arguments @(
                'iam', 'delete-role', '--profile', $Profile, '--role-name', $OperatorRoleName
            ) -ResourceTokens @($OperatorRoleName)
        }
        elseif ((Get-AwsErrorCode -Output $operatorCheck.Output) -ne 'NoSuchEntity' -or
            -not (Confirm-MutationFromHistory -State $State `
                -OperationId 'final-delete-operator-role')) {
            throw 'A ausência da role temporária não pôde ser reconciliada.'
        }
        $State.Created.OperatorRole = $false
        Save-State -State $State
    }

    $State.Cleanup.Status = 'completed'
    $State.Cleanup.Residues = @(@($State.KeyAArn, $State.KeyBArn) | Where-Object { $_ })
    $State.Status = 'cleaned-awaiting-verification'
    Add-TestResult -State $State -Item 12 -Verdict 'PASS' `
        -Evidence 'Recursos removidos; as CMKs criadas foram confirmadas em PendingDeletion.'
    Save-State -State $State
    [pscustomobject]@{
        Status = $State.Status
        PendingDeletionKeys = @($State.Cleanup.Residues).Count
        OperatorRoleDeleted = -not $State.Created.OperatorRole
        CloudTrailEvents = $FinalCorpusEvents
        FinalAuditEventsPending = @($State.Cleanup.ExpectedFinalEvents).Count
    }
}

function Start-CleanupFinalization {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $State,
        [int] $FinalCorpusEvents = 0
    )

    $indeterminate = @(Get-IndeterminateMutationIds -State $State)
    if ($indeterminate.Count -gt 0) {
        throw "A finalização foi recusada por mutações indeterminadas: $($indeterminate -join ', ')."
    }
    $State.Cleanup.FinalCorpusEvents = $FinalCorpusEvents
    $State.Cleanup.Status = 'finalizing'
    $State.Status = 'cleanup-finalizing'
    Save-State -State $State
}

if ($Phase -eq 'Cleanup') {
    Initialize-StateStorage
    $state = Read-State
    $script:ActiveState = $state
    if ($state.AccountId -ne $ExpectedAccountId -or $state.RunId -ne $RunId) {
        throw 'O estado restrito não pertence à conta e ao RunId autorizados.'
    }
    if ($state.Status -eq 'cleanup-finalizing' -or $state.Cleanup.Status -eq 'finalizing') {
        if ($state.Status -ne 'cleanup-finalizing' -or $state.Cleanup.Status -ne 'finalizing') {
            throw 'O estado e o subestado da finalização do cleanup divergiram.'
        }
        $finalizationCredential = if ($operatorRoleAbsentForCleanup -or
            $operatorPolicyAbsentForCleanup) { $null } else { $operatorCredential }
        Complete-CleanupFinalization -State $state -Credential $finalizationCredential `
            -FinalCorpusEvents ([int]$state.Cleanup.FinalCorpusEvents) |
            ConvertTo-Json -Compress
        exit 0
    }

    $indeterminateProvisionOperations = @(Resolve-ProvisionResources -State $state)
    if ($indeterminateProvisionOperations.Count -gt 0) {
        $state.Cleanup.Status = 'pending-provision-reconciliation'
        $state.Cleanup.IndeterminateProvisionOperations = $indeterminateProvisionOperations
        $state.Status = 'cleanup-pending'
        Save-State -State $state
        [pscustomobject]@{
            Status = $state.Status
            Reason = 'indeterminate-provision-mutations'
            Operations = $indeterminateProvisionOperations
        } | ConvertTo-Json -Compress
        exit 4
    }

    if ($state.Cleanup.Status -eq 'awaiting-final-trail-delivery') {
        $pendingHistoryReconciliation = @(Resolve-ManagementCleanupMutationIntents `
            -State $state -StopLoggingOnly)
        $stopExpected = @($state.ExpectedEvents | Where-Object {
            $_.Item -eq '12' -and $_.Action -eq 'CleanupStopLogging'
        } | Select-Object -First 1)[0]
        if (-not $stopExpected) {
            throw 'A parada do trail não possui expectativa autenticada.'
        }
        $stopMutation = Get-Mutation -State $state `
            -OperationId "matrix:$($stopExpected.CallId)"
        if (-not $stopMutation -or -not $stopMutation.EventTime) {
            [pscustomobject]@{
                Status = 'awaiting-final-trail-delivery'
                Reason = 'stop-event-history-reconciliation-pending'
                Operations = @("matrix:$($stopExpected.CallId)")
            } | ConvertTo-Json -Compress
            exit 4
        }
        if ($pendingHistoryReconciliation.Count -gt 0) {
            [pscustomobject]@{
                Status = 'awaiting-final-trail-delivery'
                Reason = 'event-history-reconciliation-pending'
                Operations = $pendingHistoryReconciliation
            } | ConvertTo-Json -Compress
            exit 4
        }
        $state.Cleanup.StopRequestedAt = $stopMutation.EventTime
        Save-State -State $state
        $stopRequestedAt = ConvertFrom-IsoTimestamp -Value $state.Cleanup.StopRequestedAt
        $trailStatus = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
            'cloudtrail', 'get-trail-status', '--name', $state.TrailName,
            '--region', $Region, '--output', 'json'
        )
        if ($trailStatus.IsLogging) {
            throw 'O trail continua registrando após o pedido de parada.'
        }
        $lastDelivery = if ($trailStatus.LatestDeliveryTime) {
            ConvertFrom-IsoTimestamp -Value $trailStatus.LatestDeliveryTime
        }
        else { [System.DateTimeOffset]::MinValue }
        $lastDigestDelivery = if ($trailStatus.LatestDigestDeliveryTime) {
            ConvertFrom-IsoTimestamp -Value $trailStatus.LatestDigestDeliveryTime
        }
        else { [System.DateTimeOffset]::MinValue }
        if (-not [string]::IsNullOrWhiteSpace($trailStatus.LatestDeliveryError) -or
            -not [string]::IsNullOrWhiteSpace($trailStatus.LatestDigestDeliveryError)) {
            throw 'O CloudTrail reportou erro na entrega final de log ou digest.'
        }
        if ($lastDelivery -lt $stopRequestedAt -or $lastDigestDelivery -lt $stopRequestedAt) {
            [pscustomobject]@{
                Status = 'awaiting-final-trail-delivery'
                LatestDeliveryTime = if ($lastDelivery -eq [System.DateTimeOffset]::MinValue) {
                    $null
                }
                else { ConvertTo-CanonicalUtcTimestamp -Value $lastDelivery }
                LatestDigestDeliveryTime = if (
                    $lastDigestDelivery -eq [System.DateTimeOffset]::MinValue
                ) {
                    $null
                }
                else { ConvertTo-CanonicalUtcTimestamp -Value $lastDigestDelivery }
            } | ConvertTo-Json -Compress
            exit 4
        }

        $finalCorpus = Get-CloudTrailCorpus -Credential $operatorCredential -State $state
        if ($finalCorpus.Records.Count -eq 0 -or $finalCorpus.LogKeys.Count -eq 0 -or
            $finalCorpus.DigestKeys.Count -eq 0) {
            [pscustomobject]@{ Status = 'awaiting-final-trail-delivery'; Reason = 'incomplete-log-or-digest-corpus' } | ConvertTo-Json -Compress
            exit 4
        }
        Assert-LocalCloudTrailManifest -State $state

        $cleanupCorrelation = Get-CloudTrailCorrelationResult `
            -State $state -Records @($finalCorpus.Records) `
            -ExpectedEvents @($state.ExpectedEvents | Where-Object Item -eq '12')
        Complete-CleanupMutationsFromCorrelations -State $state `
            -Correlations @($cleanupCorrelation.Correlations)
        foreach ($correlation in @($cleanupCorrelation.Correlations)) {
            Write-RestrictedEvent -Entry $correlation
        }
        if ($cleanupCorrelation.MissingCallIds.Count -gt 0) {
            [pscustomobject]@{
                Status = 'awaiting-final-trail-delivery'
                MissingCallIds = @($cleanupCorrelation.MissingCallIds)
            } | ConvertTo-Json -Compress
            exit 4
        }
        $indeterminateCleanupMutations = @(Get-IndeterminateMutationIds -State $state)
        if ($indeterminateCleanupMutations.Count -gt 0) {
            [pscustomobject]@{
                Status = 'awaiting-final-trail-delivery'
                Reason = 'indeterminate-cleanup-mutations'
                Operations = $indeterminateCleanupMutations
            } | ConvertTo-Json -Compress
            exit 4
        }

        $validation = Invoke-Aws -Credential $operatorCredential -AllowFailure -Arguments @(
            'cloudtrail', 'validate-logs', '--trail-arn', $state.TrailArn,
            '--start-time', $state.ProvisionStartedAt,
            '--end-time', (ConvertTo-CanonicalUtcTimestamp `
                -Value ([System.DateTimeOffset]::UtcNow)),
            '--s3-bucket', $state.TrailBucket, '--account-id', $ExpectedAccountId,
            '--verbose', '--region', $Region
        )
        if ($validation.ExitCode -ne 0 -or $validation.Output -match '(?i)INVALID' -or
            $validation.Output -notmatch '(?m)([1-9][0-9]*)/\1 digest files valid' -or
            $validation.Output -notmatch '(?m)([1-9][0-9]*)/\1 log files valid') {
            [pscustomobject]@{
                Status = 'awaiting-final-trail-delivery'
                Reason = 'A cadeia final de digests e logs ainda não foi validada.'
            } | ConvertTo-Json -Compress
            exit 4
        }
        $state.Cleanup.DigestValidation = $validation.Output
        $state.Cleanup.LastAuditedMutationAt = $state.Cleanup.StopRequestedAt
        Start-CleanupFinalization -State $state -FinalCorpusEvents $finalCorpus.Records.Count

        Complete-CleanupFinalization -State $state -Credential $operatorCredential `
            -FinalCorpusEvents $finalCorpus.Records.Count | ConvertTo-Json -Compress
        exit 0
    }

    if ($state.Status -notin @(
        'evidence-collected-awaiting-cleanup', 'cleanup-pending',
        'exercise-failed', 'exercise-running', 'provision-failed', 'provisioning',
        'provisioned-not-verified', 'exercised-awaiting-evidence', 'evidence-pending',
        'cleanup-running'
    )) {
        throw "Cleanup não aceita o estado $($state.Status)."
    }

    $state.Cleanup.Status = 'running'
    $state.Status = 'cleanup-running'
    Save-State -State $state

    if ($state.Created.ObjectBucket) {
        $objectBucketCheck = Invoke-ProfileAws -Arguments @(
            's3api', 'head-bucket', '--profile', $Profile, '--bucket', $state.ObjectBucket,
            '--expected-bucket-owner', $ExpectedAccountId
        )
        if ($objectBucketCheck.ExitCode -ne 0) {
            if ((Get-AwsErrorCode -Output $objectBucketCheck.Output) -notin @('404', 'NoSuchBucket')) {
                throw 'Não foi possível determinar se o bucket de objetos ainda existe.'
            }
            $deleteObjectBucketArguments = @(
                's3api', 'delete-bucket', '--bucket', $state.ObjectBucket, '--region', $Region
            )
            $deleteObjectBucketCallId = Get-DeterministicCallId -Item '12' `
                -Actor 'Operator' -Action 'CleanupDeleteObjectBucket' `
                -Arguments $deleteObjectBucketArguments
            if (-not (Confirm-MutationFromHistory -State $state `
                -OperationId "matrix:$deleteObjectBucketCallId")) {
                throw 'A ausência do bucket de objetos não pôde ser reconciliada.'
            }
            $state.Created.ObjectBucket = $false
            Save-State -State $state
        }
        else {
        Assert-BucketOwned -Credential $operatorCredential -Bucket $state.ObjectBucket `
            -State $state -CreateOperationId 'create-object-bucket'
        $protectedVersion = if ($state.Objects) { $state.Objects.V1VersionId } else { $null }
        $blocked = @(Remove-BucketVersions -Credential $operatorCredential `
            -Bucket $state.ObjectBucket -ExpectedOwner $ExpectedAccountId `
            -ProtectedVersionId $protectedVersion -AllowGovernanceBypass)
        if ($blocked.Count -gt 0) {
            $state.Cleanup.Status = 'cleanup-pending'
            $state.Cleanup.Residues = $blocked
            $state.Status = 'cleanup-pending'
            Save-State -State $state
            [pscustomobject]@{
                Status = $state.Status
                Residues = $blocked.Count
                RetainUntil = $state.Objects.ComplianceRetainUntil
            } | ConvertTo-Json -Compress
            exit 4
        }

        if ($protectedVersion) {
            $validatorCredential = Get-DataCredential -OperatorCredential $operatorCredential `
                -RoleArn $state.Roles.ValidatorA.Arn -SessionName 'cleanup-validator'
            $protectedPath = Join-Path $StateRoot 'cleanup-protected-v1.bin'
            Invoke-MatrixCall -Item '12' -Actor 'ValidatorA' -Action 'RevalidateProtectedV1' `
                -Credential $validatorCredential -ExpectSuccess $true -Arguments @(
                    's3api', 'get-object', '--bucket', $state.ObjectBucket,
                    '--key', $state.Objects.IdentityKey, '--version-id', $protectedVersion,
                    $protectedPath, '--output', 'json'
                ) | Out-Null
            Assert-Integrity -Expected $state.IntegrityA -ActualPath $protectedPath `
                -Message 'V1 falhou na revalidação anterior ao cleanup.'
            Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupDeleteProtectedV1' `
                -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                    's3api', 'delete-object', '--bucket', $state.ObjectBucket,
                    '--key', $state.Objects.IdentityKey, '--version-id', $protectedVersion
                ) | Out-Null
        }

        $remainingBlocked = @(Remove-BucketVersions -Credential $operatorCredential `
            -Bucket $state.ObjectBucket -ExpectedOwner $ExpectedAccountId -AllowGovernanceBypass)
        if ($remainingBlocked.Count -gt 0) {
            $state.Cleanup.Status = 'cleanup-pending'
            $state.Cleanup.Residues = $remainingBlocked
            $state.Status = 'cleanup-pending'
            Save-State -State $state
            [pscustomobject]@{ Status = $state.Status; Residues = $remainingBlocked.Count } | ConvertTo-Json -Compress
            exit 4
        }
        $remainingVersions = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
            's3api', 'list-object-versions', '--bucket', $state.ObjectBucket, '--output', 'json'
        )
        $remainingVersionItems = @($remainingVersions.Versions | Where-Object { $null -ne $_ })
        $remainingMarkerItems = @($remainingVersions.DeleteMarkers | Where-Object { $null -ne $_ })
        if ($remainingVersionItems.Count -gt 0 -or $remainingMarkerItems.Count -gt 0) {
            throw 'O bucket versionado ainda contém versões ou delete markers.'
        }
        Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupDeleteObjectBucket' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                's3api', 'delete-bucket', '--bucket', $state.ObjectBucket, '--region', $Region
            ) | Out-Null
        Write-Journal -Type 'delete' -ResourceType 's3-bucket' -ResourceId $state.ObjectBucket -Status 'succeeded'
        $state.Created.ObjectBucket = $false
        Save-State -State $state
        }
    }

    foreach ($roleName in @($state.Created.RoleNames)) {
        $roleCheck = Invoke-ProfileAws -Arguments @(
            'iam', 'get-role', '--profile', $Profile, '--role-name', $roleName, '--output', 'json'
        )
        if ($roleCheck.ExitCode -ne 0) {
            if ((Get-AwsErrorCode -Output $roleCheck.Output) -ne 'NoSuchEntity') {
                throw "Não foi possível determinar se a role $roleName ainda existe."
            }
            $deleteRoleArguments = @('iam', 'delete-role', '--role-name', $roleName)
            $deleteRoleCallId = Get-DeterministicCallId -Item '12' -Actor 'Operator' `
                -Action 'CleanupDeleteDataRole' -Arguments $deleteRoleArguments
            if (-not (Confirm-MutationFromHistory -State $state `
                -OperationId "matrix:$deleteRoleCallId")) {
                throw "A ausência da role $roleName não pôde ser reconciliada."
            }
            $state.Created.RoleNames = @($state.Created.RoleNames | Where-Object { $_ -ne $roleName })
            Save-State -State $state
            continue
        }
        Assert-RoleOwned -Credential $operatorCredential -RoleName $roleName
        $policies = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
            'iam', 'list-role-policies', '--role-name', $roleName, '--output', 'json'
        )
        foreach ($policyName in @($policies.PolicyNames)) {
            Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupDeleteDataRolePolicy' `
                -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                    'iam', 'delete-role-policy', '--role-name', $roleName, '--policy-name', $policyName
                ) | Out-Null
        }
        Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupDeleteDataRole' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                'iam', 'delete-role', '--role-name', $roleName
            ) | Out-Null
        Write-Journal -Type 'delete' -ResourceType 'iam-role' -ResourceId $roleName -Status 'succeeded'
        $state.Created.RoleNames = @($state.Created.RoleNames | Where-Object { $_ -ne $roleName })
        Save-State -State $state
    }

    foreach ($aliasName in @($state.Created.AliasNames)) {
        $aliasListing = Invoke-ProfileAws -Arguments @(
            'kms', 'list-aliases', '--profile', $Profile, '--region', $Region, '--output', 'json'
        )
        if ($aliasListing.ExitCode -ne 0) {
            throw "Não foi possível inventariar o alias $aliasName."
        }
        if ((($aliasListing.Output | ConvertFrom-Json).Aliases.AliasName) -notcontains $aliasName) {
            $deleteAliasArguments = @(
                'kms', 'delete-alias', '--alias-name', $aliasName, '--region', $Region
            )
            $deleteAliasCallId = Get-DeterministicCallId -Item '12' -Actor 'Operator' `
                -Action 'CleanupDeleteAlias' -Arguments $deleteAliasArguments
            if (-not (Confirm-MutationFromHistory -State $state `
                -OperationId "matrix:$deleteAliasCallId")) {
                throw "A ausência do alias $aliasName não pôde ser reconciliada."
            }
            $state.Created.AliasNames = @($state.Created.AliasNames | Where-Object { $_ -ne $aliasName })
            Save-State -State $state
            continue
        }
        $keyArn = if ($aliasName -eq $state.KeyAAlias) { $state.KeyAArn } else { $state.KeyBArn }
        Assert-KeyOwned -Credential $operatorCredential -KeyArn $keyArn
        Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupDeleteAlias' `
            -Credential $operatorCredential -ExpectSuccess $true -Arguments @(
                'kms', 'delete-alias', '--alias-name', $aliasName, '--region', $Region
            ) | Out-Null
        Write-Journal -Type 'delete' -ResourceType 'kms-alias' -ResourceId $aliasName -Status 'succeeded'
        $state.Created.AliasNames = @($state.Created.AliasNames | Where-Object { $_ -ne $aliasName })
        Save-State -State $state
    }

    foreach ($keyArn in @($state.KeyAArn, $state.KeyBArn)) {
        if (-not $keyArn) { continue }
        Assert-KeyOwned -Credential $operatorCredential -KeyArn $keyArn
        $description = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
            'kms', 'describe-key', '--key-id', $keyArn, '--region', $Region, '--output', 'json'
        )
        $scheduleArguments = @(
            'kms', 'schedule-key-deletion', '--key-id', $keyArn,
            '--pending-window-in-days', '7', '--region', $Region
        )
        $scheduleCallId = Get-DeterministicCallId -Item '12' -Actor 'Operator' `
            -Action 'CleanupScheduleKeyDeletion' -Arguments $scheduleArguments
        if ($description.KeyMetadata.KeyState -ne 'PendingDeletion') {
            Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupScheduleKeyDeletion' `
                -Credential $operatorCredential -ExpectSuccess $true `
                -Arguments $scheduleArguments | Out-Null
        }
        elseif (-not (Get-Mutation -State $state -OperationId "matrix:$scheduleCallId") -or
            -not (Confirm-MutationFromHistory -State $state `
                -OperationId "matrix:$scheduleCallId")) {
            $state.Cleanup.Status = 'cleanup-pending'
            $state.Status = 'cleanup-pending'
            Save-State -State $state
            [pscustomobject]@{
                Status = $state.Status
                Reason = 'schedule-key-deletion-event-pending'
                OperationId = "matrix:$scheduleCallId"
            } | ConvertTo-Json -Compress
            exit 4
        }
        $pending = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
            'kms', 'describe-key', '--key-id', $keyArn, '--region', $Region, '--output', 'json'
        )
        if ($pending.KeyMetadata.KeyState -ne 'PendingDeletion' -or -not $pending.KeyMetadata.DeletionDate) {
            throw 'A CMK não confirmou PendingDeletion e DeletionDate.'
        }
        Write-Journal -Type 'schedule-deletion' -ResourceType 'kms-key' -ResourceId $keyArn -Status 'succeeded'
    }

    if ($state.Created.Trail -and $state.Created.TrailLoggingStarted) {
        Assert-TrailOwned -Credential $operatorCredential -TrailArn $state.TrailArn
        $stopArguments = @(
            'cloudtrail', 'stop-logging', '--name', $state.TrailName, '--region', $Region
        )
        $stopStatus = Invoke-AwsJson -Credential $operatorCredential -Arguments @(
            'cloudtrail', 'get-trail-status', '--name', $state.TrailName,
            '--region', $Region, '--output', 'json'
        )
        $stopCallId = Get-DeterministicCallId -Item '12' -Actor 'Operator' `
            -Action 'CleanupStopLogging' -Arguments $stopArguments
        if ($stopStatus.IsLogging) {
            Invoke-MatrixCall -Item '12' -Actor 'Operator' -Action 'CleanupStopLogging' `
                -Credential $operatorCredential -ExpectSuccess $true -Arguments $stopArguments | Out-Null
        }
        else {
            Confirm-MutationFromHistory -State $state `
                -OperationId "matrix:$stopCallId" | Out-Null
        }
        $stopMutation = Get-Mutation -State $state -OperationId "matrix:$stopCallId"
        if (-not $stopMutation) {
            throw 'A parada do trail ocorreu sem mutação autenticada correspondente.'
        }
        $state.Cleanup.StopRequestedAt = $stopMutation.EventTime
        $state.Cleanup.Status = 'awaiting-final-trail-delivery'
        $state.Status = 'cleanup-awaiting-final-trail'
        Save-State -State $state
        [pscustomobject]@{
            Status = $state.Status
            Reason = if ($stopMutation.EventTime) {
                'final-trail-delivery-pending'
            }
            else { 'stop-event-history-reconciliation-pending' }
            RetryAfter = ConvertTo-CanonicalUtcTimestamp -Value (
                [System.DateTimeOffset]::UtcNow.AddMinutes(5)
            )
        } | ConvertTo-Json -Compress
        exit 4
    }
    $indeterminateCleanupMutations = @(Resolve-ManagementCleanupMutationIntents -State $state)
    if ($indeterminateCleanupMutations.Count -gt 0) {
        $state.Cleanup.Status = 'cleanup-pending'
        $state.Status = 'cleanup-pending'
        Save-State -State $state
        [pscustomobject]@{
            Status = $state.Status
            Reason = 'indeterminate-cleanup-mutations'
            Operations = $indeterminateCleanupMutations
        } | ConvertTo-Json -Compress
        exit 4
    }
    Start-CleanupFinalization -State $state
    Complete-CleanupFinalization -State $state -Credential $operatorCredential |
        ConvertTo-Json -Compress
    exit 0
}

if ($Phase -eq 'VerifyCleanup') {
    Initialize-StateStorage
    $state = Read-State
    if ($state.AccountId -ne $ExpectedAccountId -or $state.RunId -ne $RunId) {
        throw 'O estado restrito não pertence à conta e ao RunId autorizados.'
    }
    if ($state.Status -ne 'cleaned-awaiting-verification') {
        throw "VerifyCleanup não aceita o estado $($state.Status)."
    }
    $testResults = @($state.Tests | Where-Object { $null -ne $_ })
    $cleanupResults = @($testResults | Where-Object { [string]$_.Item -eq '12' })
    $exerciseResults = @($testResults | Where-Object { [string]$_.Item -ne '12' })
    $manifestEntries = @($state.Evidence.CloudTrailManifest |
        Where-Object { $null -ne $_ })
    $isPartialProvisionCleanup = -not $state.Contains('ExerciseStartedAt') -and
        [string]::IsNullOrWhiteSpace([string]$state.ExerciseCompletedAt) -and
        $exerciseResults.Count -eq 0 -and
        $cleanupResults.Count -eq 1 -and $cleanupResults[0].Verdict -eq 'PASS' -and
        $state.Cleanup.Contains('FinalCorpusEvents') -and
        [int]$state.Cleanup.FinalCorpusEvents -eq 0
    if ($isPartialProvisionCleanup) {
        if ($manifestEntries.Count -gt 0) {
            throw 'O provisionamento parcial possui um manifesto do CloudTrail incompatível.'
        }
    }
    else {
        Assert-LocalCloudTrailManifest -State $state
    }

    $residues = @()
    foreach ($bucket in @($state.ObjectBucket, $state.TrailBucket)) {
        $bucketCheck = Invoke-ProfileAws -Arguments @(
            's3api', 'head-bucket', '--profile', $Profile, '--bucket', $bucket,
            '--expected-bucket-owner', $ExpectedAccountId
        )
        if ($bucketCheck.ExitCode -eq 0) {
            $residues += "bucket:$bucket"
        }
        else {
            $errorCode = Get-AwsErrorCode -Output $bucketCheck.Output
            if ($errorCode -notin @('404', 'NoSuchBucket')) {
                throw "A verificação do bucket falhou sem comprovar ausência: $errorCode."
            }
        }
    }
    foreach ($roleName in @($state.DataRoleNames) + $state.OperatorRoleName) {
        $roleCheck = Invoke-ProfileAws -Arguments @(
            'iam', 'get-role', '--profile', $Profile, '--role-name', $roleName, '--output', 'json'
        )
        if ($roleCheck.ExitCode -eq 0) {
            $residues += "role:$roleName"
        }
        else {
            $errorCode = Get-AwsErrorCode -Output $roleCheck.Output
            if ($errorCode -ne 'NoSuchEntity') {
                throw "A verificação da role falhou sem comprovar ausência: $errorCode."
            }
        }
    }
    $trailListing = Invoke-ProfileAws -Arguments @(
        'cloudtrail', 'list-trails', '--profile', $Profile, '--region', $Region, '--output', 'json'
    )
    if ($trailListing.ExitCode -ne 0) { throw "Falha ao verificar trails: $($trailListing.Output)" }
    $trails = $trailListing.Output | ConvertFrom-Json
    if ($trails.Trails.Name -contains $state.TrailName) { $residues += "trail:$($state.TrailName)" }
    $aliasListing = Invoke-ProfileAws -Arguments @(
        'kms', 'list-aliases', '--profile', $Profile, '--region', $Region, '--output', 'json'
    )
    if ($aliasListing.ExitCode -ne 0) { throw "Falha ao verificar aliases: $($aliasListing.Output)" }
    $aliases = $aliasListing.Output | ConvertFrom-Json
    foreach ($aliasName in @($state.KeyAAlias, $state.KeyBAlias)) {
        if ($aliases.Aliases.AliasName -contains $aliasName) { $residues += "alias:$aliasName" }
    }

    $pendingKeys = @()
    foreach ($keyArn in @($state.KeyAArn, $state.KeyBArn)) {
        if (-not $keyArn) { continue }
        $keyCheck = Invoke-ProfileAws -Arguments @(
            'kms', 'describe-key', '--profile', $Profile, '--key-id', $keyArn,
            '--region', $Region, '--output', 'json'
        )
        if ($keyCheck.ExitCode -ne 0) {
            throw "Falha ao verificar a CMK em PendingDeletion: $($keyCheck.Output)"
        }
        $key = $keyCheck.Output | ConvertFrom-Json
        if (
            $key.KeyMetadata.KeyState -ne 'PendingDeletion' -or
            -not $key.KeyMetadata.DeletionDate) {
            $residues += "kms-not-pending:$keyArn"
        }
        else {
            $pendingKeys += [ordered]@{
                KeyArn = $keyArn
                State = $key.KeyMetadata.KeyState
                DeletionDate = $key.KeyMetadata.DeletionDate
            }
        }
    }
    if ($residues.Count -gt 0) {
        $state.Status = 'cleanup-verification-failed'
        $state.Cleanup.Residues = $residues
        Save-State -State $state
        throw "A verificação final encontrou resíduos inesperados: $($residues -join ', ')"
    }

    $completedFinalEvents = @($state.Cleanup.ExpectedFinalEvents |
        Where-Object Status -eq 'completed')
    $finalOperationIds = @($state.Cleanup.ExpectedFinalEvents |
        ForEach-Object OperationId | Sort-Object -Unique)
    $operationsWithoutCompletedAudit = @($finalOperationIds | Where-Object {
        $operationId = $_
        @($completedFinalEvents | Where-Object OperationId -eq $operationId).Count -eq 0
    })
    if ($operationsWithoutCompletedAudit.Count -gt 0) {
        [pscustomobject]@{
            Status = 'final-audit-pending'
            MissingOperations = $operationsWithoutCompletedAudit
        } | ConvertTo-Json -Compress
        exit 4
    }
    if ($completedFinalEvents.Count -gt 0) {
        $auditStart = ($completedFinalEvents | ForEach-Object {
            ConvertFrom-IsoTimestamp -Value $_.StartedAt
        } | Sort-Object | Select-Object -First 1).AddMinutes(-2)
        $historyResult = Invoke-ProfileAws -Arguments @(
            'cloudtrail', 'lookup-events', '--profile', $Profile, '--region', $Region,
            '--start-time', (ConvertTo-CanonicalUtcTimestamp -Value $auditStart),
            '--end-time', (ConvertTo-CanonicalUtcTimestamp `
                -Value ([System.DateTimeOffset]::UtcNow)),
            '--output', 'json'
        )
        if ($historyResult.ExitCode -ne 0) {
            throw "Falha ao consultar o histórico final: $($historyResult.Output)"
        }
        $history = $historyResult.Output | ConvertFrom-Json -DateKind String
        $finalCorrelation = Get-FinalHistoryCorrelationResult `
            -History $history -ExpectedEvents $completedFinalEvents
        Complete-FinalAuditExpectationsFromCorrelations -State $state `
            -Correlations @($finalCorrelation.Correlations)
        foreach ($correlation in @($finalCorrelation.Correlations)) {
            Write-RestrictedEvent -Entry $correlation
        }
        if ($finalCorrelation.MissingAttemptIds.Count -gt 0) {
            [pscustomobject]@{
                Status = 'final-audit-pending'
                MissingAttempts = $finalCorrelation.MissingAttemptIds
            } | ConvertTo-Json -Compress
            exit 4
        }
    }

    if ($isPartialProvisionCleanup) {
        $state.Status = 'cleanup-verified-partial-provision'
        $state.Cleanup.Status = 'verified'
        Save-State -State $state
        [pscustomobject]@{
            Status = $state.Status
            PassedItems = 0
            UnexpectedResidues = 0
            PendingDeletionKeys = $pendingKeys.Count
            OperatorRoleAbsent = $true
            BucketsAbsent = $true
            TrailAbsent = $true
            DataRolesAbsent = $true
            AliasesAbsent = $true
        } | ConvertTo-Json -Compress
        exit 0
    }

    $passedItems = @($state.Tests | Where-Object Verdict -eq 'PASS' |
        Select-Object -ExpandProperty Item -Unique)
    if ($passedItems.Count -ne 12) {
        throw "A matriz não contém 12 itens PASS; observados: $($passedItems -join ', ')."
    }

    $state.Status = 'verified-complete'
    $state.Cleanup.Status = 'verified'
    Save-State -State $state
    [pscustomobject]@{
        Status = $state.Status
        PassedItems = 12
        UnexpectedResidues = 0
        PendingDeletionKeys = $pendingKeys.Count
        OperatorRoleAbsent = $true
        BucketsAbsent = $true
        TrailAbsent = $true
        DataRolesAbsent = $true
        AliasesAbsent = $true
    } | ConvertTo-Json -Compress
}

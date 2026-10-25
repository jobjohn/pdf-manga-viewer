param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\')
$outputPath = [System.IO.Path]::GetFullPath($Output)

function Get-RelativePath([string]$fullPath) {
    return $fullPath.Substring($root.Length).TrimStart('\')
}

function Get-ShortId([string]$prefix, [string]$name) {
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $hash = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($name.ToLowerInvariant()))
        $hex = -join ($hash[0..7] | ForEach-Object { $_.ToString('x2') })
        return "$prefix$hex"
    }
    finally {
        $sha1.Dispose()
    }
}

function Get-StableGuid([string]$name) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("PdfMangaViewer|$name"))
        return ([System.Guid]::new([byte[]]$hash)).ToString().ToUpperInvariant()
    }
    finally {
        $md5.Dispose()
    }
}

function Get-ParentRelative([string]$relativePath) {
    $index = $relativePath.LastIndexOf('\')
    if ($index -lt 0) {
        return ''
    }

    return $relativePath.Substring(0, $index)
}

# Build a directory tree.
$nodes = @{}
$rootNode = [pscustomobject]@{
    RelativePath = ''
    Name         = ''
    Id           = 'INSTALLFOLDER'
    Children     = [System.Collections.ArrayList]::new()
    Files        = [System.Collections.ArrayList]::new()
}
$nodes[''] = $rootNode

$directories = @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Force)
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName)

foreach ($dir in $directories) {
    $relative = Get-RelativePath $dir.FullName
    $node = [pscustomobject]@{
        RelativePath = $relative
        Name         = $dir.Name
        Id           = Get-ShortId 'dir_' $relative
        Children     = [System.Collections.ArrayList]::new()
        Files        = [System.Collections.ArrayList]::new()
    }
    $nodes[$relative] = $node
}

foreach ($dir in $directories) {
    $relative = Get-RelativePath $dir.FullName
    $parentRelative = Get-ParentRelative $relative
    if (-not $nodes.ContainsKey($parentRelative)) {
        $parentRelative = ''
    }

    [void]$nodes[$parentRelative].Children.Add($nodes[$relative])
}

foreach ($file in $files) {
    $relative = Get-RelativePath $file.FullName
    $parentRelative = Get-ParentRelative $relative
    if (-not $nodes.ContainsKey($parentRelative)) {
        $parentRelative = ''
    }

    [void]$nodes[$parentRelative].Files.Add($file)
}

$componentIds = [System.Collections.Generic.List[string]]::new()

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.IndentChars = '  '
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$settings.NewLineChars = "`r`n"

$writer = [System.Xml.XmlWriter]::Create($outputPath, $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', 'http://wixtoolset.org/schemas/v4/wxs')

    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('DirectoryRef')
    $writer.WriteAttributeString('Id', 'INSTALLFOLDER')

    function Write-FileComponent([object]$node) {
        if ($node.Files.Count -eq 0) {
            return
        }

        $componentId = if ($node.RelativePath -eq '') { 'cmp_root' } else { Get-ShortId 'cmp_' $node.RelativePath }
        $guidName = if ($node.RelativePath -eq '') { 'root' } else { "dir|$($node.RelativePath)" }
        $componentIds.Add($componentId)

        $writer.WriteStartElement('Component')
        $writer.WriteAttributeString('Id', $componentId)
        $writer.WriteAttributeString('Guid', (Get-StableGuid $guidName))

        foreach ($file in ($node.Files | Sort-Object Name)) {
            $relative = Get-RelativePath $file.FullName

            $writer.WriteStartElement('File')
            $writer.WriteAttributeString('Id', (Get-ShortId 'fil_' $relative))
            $writer.WriteAttributeString('Source', $file.FullName)
            $writer.WriteEndElement()
        }

        # ユーザープロファイルへのインストールでは HKCU のレジストリ値をキーパスにする必要がある
        $writer.WriteStartElement('RegistryValue')
        $writer.WriteAttributeString('Root', 'HKCU')
        $writer.WriteAttributeString('Key', 'Software\PdfMangaViewer\Components')
        $writer.WriteAttributeString('Name', $componentId)
        $writer.WriteAttributeString('Type', 'integer')
        $writer.WriteAttributeString('Value', '1')
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()

        $writer.WriteEndElement() # Component
    }

    Write-FileComponent $rootNode

    # All user-profile directories need RemoveFolder entries (ICE64).
    # Deepest first, so children are removed before their parents.
    $writer.WriteStartElement('Component')
    $writer.WriteAttributeString('Id', 'cmp_cleanup')
    $writer.WriteAttributeString('Guid', (Get-StableGuid 'cleanup'))
    $componentIds.Add('cmp_cleanup')

    foreach ($dir in ($directories | Sort-Object { (Get-RelativePath $_.FullName).Split('\').Count } -Descending)) {
        $relative = Get-RelativePath $dir.FullName
        $writer.WriteStartElement('RemoveFolder')
        $writer.WriteAttributeString('Id', (Get-ShortId 'rm_' $relative))
        $writer.WriteAttributeString('Directory', $nodes[$relative].Id)
        $writer.WriteAttributeString('On', 'uninstall')
        $writer.WriteEndElement()
    }

    $writer.WriteStartElement('RemoveFolder')
    $writer.WriteAttributeString('Id', 'rm_installfolder')
    $writer.WriteAttributeString('Directory', 'INSTALLFOLDER')
    $writer.WriteAttributeString('On', 'uninstall')
    $writer.WriteEndElement()

    $writer.WriteStartElement('RemoveFolder')
    $writer.WriteAttributeString('Id', 'rm_programsfolder')
    $writer.WriteAttributeString('Directory', 'ProgramsFolder')
    $writer.WriteAttributeString('On', 'uninstall')
    $writer.WriteEndElement()

    $writer.WriteStartElement('RegistryValue')
    $writer.WriteAttributeString('Root', 'HKCU')
    $writer.WriteAttributeString('Key', 'Software\PdfMangaViewer\Components')
    $writer.WriteAttributeString('Name', 'cmp_cleanup')
    $writer.WriteAttributeString('Type', 'integer')
    $writer.WriteAttributeString('Value', '1')
    $writer.WriteAttributeString('KeyPath', 'yes')
    $writer.WriteEndElement()

    $writer.WriteEndElement() # Cleanup Component

    function Write-Directories([object]$node) {
        foreach ($child in ($node.Children | Sort-Object Name)) {
            $writer.WriteStartElement('Directory')
            $writer.WriteAttributeString('Id', $child.Id)
            $writer.WriteAttributeString('Name', $child.Name)

            Write-FileComponent $child
            Write-Directories $child

            $writer.WriteEndElement()
        }
    }

    Write-Directories $rootNode

    $writer.WriteEndElement() # DirectoryRef
    $writer.WriteEndElement() # Fragment

    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('ComponentGroup')
    $writer.WriteAttributeString('Id', 'AppFiles')

    foreach ($componentId in $componentIds) {
        $writer.WriteStartElement('ComponentRef')
        $writer.WriteAttributeString('Id', $componentId)
        $writer.WriteEndElement()
    }

    $writer.WriteEndElement() # ComponentGroup
    $writer.WriteEndElement() # Fragment

    $writer.WriteEndElement() # Wix
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

Write-Output "Generated $outputPath ($($files.Count) files, $($directories.Count) directories)"

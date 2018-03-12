[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Path = Join-Path $Env:APPCENTER_SOURCE_DIRECTORY "\ExViewer\AppPackages"
$Files = Get-ChildItem -Path $Path -Include @('*.cer', '*.appxbundle', '*.appx') -Recurse
$Version = $Files[0].Name.Split('_')[1]
$Auth = [convert]::ToBase64String([system.text.encoding]::UTF8.GetBytes("${ENV:GITHUB_USER}:${ENV:GITHUB_PASS}"))
$DefaultHeader = @{
    Authorization = "Basic $Auth"
}
$Releases = Invoke-WebRequest "https://api.github.com/repos/${ENV:GITHUB_USER}/ExViewer/releases" -Method Get -Headers $DefaultHeader
$ReleasesData = ConvertFrom-Json $Releases.Content
$CreateReleaseData = $ReleasesData[0]
if (-not ($CreateReleaseData.Draft -and $CreateReleaseData.Tag_Name -eq "v$Version"))
{
    $CreateRelease = Invoke-WebRequest "https://api.github.com/repos/${ENV:GITHUB_USER}/ExViewer/releases" -Method Post -Headers $DefaultHeader -Body @"
{
  "tag_name": "v$Version",
  "target_commitish": "${Env:APPCENTER_BRANCH}",
  "name": "v$Version",
  "body": "",
  "draft": true,
  "prerelease": false
}
"@
    $CreateReleaseData = ConvertFrom-Json $CreateRelease.Content
}


$DefaultHeader['Content-Type'] = 'application/octet-stream'
$Files | %{
    $UpUri = $CreateReleaseData.upload_url.Replace('{?name,label}', "?name=$($_.Name)")
    Echo $UpUri
    Invoke-WebRequest $UpUri -Method Post -Headers $DefaultHeader -InFile $_ -ErrorAction Continue
}
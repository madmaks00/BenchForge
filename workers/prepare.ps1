param([string]$Engines='java,python,rust,node,go',[string]$ToolsDirectory='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$ids=@($Engines.Split(',')|ForEach-Object {$_.Trim()}|Where-Object {$_ -and $_ -ne 'csharp'}|Select-Object -Unique)
foreach($id in $ids){if($id -notin @('java','python','rust','node','go')){throw ('Unknown engine: '+$id)}}
if(!$ToolsDirectory){$ToolsDirectory=$env:BENCHFORGE_TOOLCHAIN_HOME}
if(!$ToolsDirectory){$settings=Join-Path $env:LOCALAPPDATA 'BenchForge\toolchains.json';if(Test-Path -LiteralPath $settings){$ToolsDirectory=(Get-Content -LiteralPath $settings -Raw|ConvertFrom-Json).directory}}
if(!$ToolsDirectory){$ToolsDirectory=Join-Path $env:LOCALAPPDATA 'BenchForge\tools'}
$ToolsDirectory=[IO.Path]::GetFullPath($ToolsDirectory)
$arch=if($env:PROCESSOR_ARCHITECTURE -eq 'ARM64'){'arm64'}else{'x64'}
$dist=[IO.Path]::GetFullPath((Join-Path $root ('dist\windows-'+$arch)))
New-Item -ItemType Directory -Path $dist -Force|Out-Null
$staging=Join-Path $dist ('.prepare-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging|Out-Null
function Invoke-Checked([string]$Executable,[string[]]$Arguments){& $Executable @Arguments;if($LASTEXITCODE -ne 0){throw ('Command failed: '+[IO.Path]::GetFileName($Executable))}}
function Tool([string]$Name,[string]$Candidate){if($Candidate -and (Test-Path -LiteralPath $Candidate)){return $Candidate};$command=Get-Command $Name -ErrorAction SilentlyContinue;if(!$command){throw ('Install '+$Name+' or pass -ToolsDirectory with the portable tools. See workers/README.md.')};return $command.Source}
function Assert-OwnedPath([string]$Path){$full=[IO.Path]::GetFullPath($Path);if(!$full.StartsWith($dist+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Generated path escaped dist.'}}
$manifest=Join-Path $dist 'engines.json'
$entries=@();if(Test-Path -LiteralPath $manifest){$entries=@((Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json)|Where-Object {$_.engine -in @('java','python','rust','node','go')})}
try{
 foreach($id in $ids){
  Write-Output ('Preparing '+$id+' ...')
  $target=Join-Path $staging $id;New-Item -ItemType Directory -Path $target|Out-Null
  if($id -in @('node','go','rust')) { $nativePython=Tool 'python' ''; Invoke-Checked $nativePython @((Join-Path $root 'native-libraries.py'),$id,$target,$ToolsDirectory) }
  switch($id){
   'python'{
    $python=Tool 'python' ''
    Copy-Item -Path (Join-Path $root 'python\*.py') -Destination $target
    Invoke-Checked $python @('-m','pip','install','--disable-pip-version-check','--no-warn-script-location','--only-binary=:all:','--target',(Join-Path $target 'vendor'),'-r',(Join-Path $root 'python\requirements.lock.txt'))
    Invoke-Checked $python @((Join-Path $target 'worker.py'),'--self-test')
    $entry=@{engine=$id;executable='python';arguments=@('python/worker.py')}
   }
   'node'{
    $node=Tool 'node' ''; $npm=Tool 'npm.cmd' ''
    Copy-Item -Path (Join-Path $root 'node\*.mjs'),(Join-Path $root 'node\package*.json') -Destination $target
    Invoke-Checked $npm @('ci','--prefix',$target,'--ignore-scripts','--no-audit','--no-fund')
    Invoke-Checked $node @((Join-Path $target 'worker.mjs'),'--self-test')
    $entry=@{engine=$id;executable='node';arguments=@('node/worker.mjs')}
   }
   'java'{
    $java=Tool 'java' ''; $maven=Tool 'mvn.cmd' (Join-Path $ToolsDirectory 'apache-maven-3.9.11\bin\mvn.cmd')
    Invoke-Checked $maven @('-f',(Join-Path $root 'java\pom.xml'),('-Dmaven.repo.local='+ (Join-Path $ToolsDirectory 'maven-cache')),'-q','-DskipTests','package','dependency:copy-dependencies','-DoutputDirectory=target/lib')
    Copy-Item -LiteralPath (Join-Path $root 'java\target\worker-java-0.2.0.jar') -Destination (Join-Path $target 'worker.jar')
    Copy-Item -LiteralPath (Join-Path $root 'java\target\lib') -Destination $target -Recurse
    Invoke-Checked $java @('-cp',((Join-Path $target 'worker.jar')+';'+(Join-Path $target 'lib\*')),'dev.benchforge.Worker','--self-test')
    $entry=@{engine=$id;executable='java';arguments=@('-cp','java/worker.jar;java/lib/*','dev.benchforge.Worker')}
   }
   'go'{
    $go=Tool 'go' (Join-Path $ToolsDirectory 'go\bin\go.exe');$env:GOPATH=Join-Path $ToolsDirectory 'go-cache';$env:GOCACHE=Join-Path $ToolsDirectory 'go-build-cache'
    Push-Location (Join-Path $root 'go');try{Invoke-Checked $go @('build','-mod=readonly','-trimpath','-ldflags','-s -w','-o',(Join-Path $target 'benchforge-go.exe'),'.')}finally{Pop-Location}
    Invoke-Checked (Join-Path $target 'benchforge-go.exe') @('--self-test')
    $entry=@{engine=$id;executable='./go/benchforge-go.exe';arguments=@()}
   }
   'rust'{
    $cargo=Tool 'cargo' (Join-Path $ToolsDirectory 'cargo\bin\cargo.exe')
    if(Test-Path -LiteralPath (Join-Path $ToolsDirectory 'rustup')){$env:RUSTUP_HOME=Join-Path $ToolsDirectory 'rustup';$env:CARGO_HOME=Join-Path $ToolsDirectory 'cargo'}
    $env:CARGO_TARGET_DIR=Join-Path $ToolsDirectory 'rust-build'
    Invoke-Checked $cargo @('build','--manifest-path',(Join-Path $root 'rust\Cargo.toml'),'--locked','--release','-j','2')
    Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'release\benchforge-worker-rust.exe') -Destination (Join-Path $target 'benchforge-rust.exe')
    Invoke-Checked (Join-Path $target 'benchforge-rust.exe') @('--self-test')
    $entry=@{engine=$id;executable='./rust/benchforge-rust.exe';arguments=@()}
   }
  }
  $destination=Join-Path $dist $id;$backup=Join-Path $dist ('.old-'+$id+'-'+[guid]::NewGuid().ToString('N'))
  Assert-OwnedPath $destination;Assert-OwnedPath $backup;Assert-OwnedPath $target
  if(Test-Path -LiteralPath $destination){Move-Item -LiteralPath $destination -Destination $backup}
  try{Move-Item -LiteralPath $target -Destination $destination}catch{if(Test-Path -LiteralPath $backup){Move-Item -LiteralPath $backup -Destination $destination};throw}
  $entry.version='native-workers-v2'
  $entries=@($entries|Where-Object {$_.engine -ne $id})+@($entry)
  $json=ConvertTo-Json -InputObject @($entries) -Depth 8
  $temporary=Join-Path $dist 'engines.json.tmp';[IO.File]::WriteAllText($temporary,$json,(New-Object Text.UTF8Encoding($false)))
  Move-Item -LiteralPath $temporary -Destination $manifest -Force
  if(Test-Path -LiteralPath $backup){
   try { Remove-Item -LiteralPath $backup -Recurse -Force }
   catch [IO.IOException] { Write-Warning ('The new '+$id+' bundle is ready. An older loaded copy remains in '+$backup+'. Close active workers before removing it.') }
   catch [UnauthorizedAccessException] { Write-Warning ('The new '+$id+' bundle is ready. An older loaded copy remains in '+$backup+'. Close active workers before removing it.') }
  }
  Write-Output ($id+' ready.')
 }
}finally{Assert-OwnedPath $staging;if(Test-Path -LiteralPath $staging){Remove-Item -LiteralPath $staging -Recurse -Force}}
Write-Output 'Worker bundles prepared. Build BenchForge.slnx again to include them in the app output.'

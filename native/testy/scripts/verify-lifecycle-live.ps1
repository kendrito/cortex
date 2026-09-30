#requires -Version 7.0
param(
 [Parameter(Mandatory=$true)][string]$ArtifactsDirectory,
 [Parameter(Mandatory=$true)][string]$Dotnet,
 [Parameter(Mandatory=$true)][string]$Cli,
 [Parameter(Mandatory=$true)][string]$TerraSettings,
 [Parameter(Mandatory=$true)][string]$GeminiSettings
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=[IO.Path]::GetFullPath($ArtifactsDirectory)
if(Test-Path -LiteralPath $root){throw 'Choose a new evidence directory; qualification attempts are never overwritten.'}
$Dotnet=[IO.Path]::GetFullPath($Dotnet);$Cli=[IO.Path]::GetFullPath($Cli)
foreach($file in @($Dotnet,$Cli,$TerraSettings,$GeminiSettings)){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Required qualification input does not exist: $file"}}
$null=[IO.Directory]::CreateDirectory($root)
$env:DOTNET_ROOT=Split-Path -Parent $Dotnet
$scannerPath=Join-Path $PSScriptRoot 'screenshot-content.ps1'
$scanner=[IO.File]::ReadAllText($scannerPath)
$pathConstructor='$bitmap = [System.Drawing.Bitmap]::new([System.IO.Path]::GetFullPath($Path))'
$bitmapCleanup='finally { $bitmap.Dispose() }'
if(-not $scanner.Contains($pathConstructor) -or -not $scanner.Contains($bitmapCleanup)){throw 'Fixture scanner loader changed; review the byte-loader adaptation before qualification.'}
# Preserve the validated pixel checks; only avoid GDI+ filename length limitations.
Invoke-Expression ($scanner.Replace($pathConstructor,'$imageStream = [System.IO.MemoryStream]::new([System.IO.File]::ReadAllBytes([System.IO.Path]::GetFullPath($Path))); $bitmap = [System.Drawing.Bitmap]::new($imageStream)').Replace($bitmapCleanup,'finally { $bitmap.Dispose(); $imageStream.Dispose() }'))
$report=[ordered]@{schema='testy.lifecycle-live-qualification.v1';startedAt=[datetimeoffset]::UtcNow;finishedAt=$null;passed=$false;cli=$Cli;cliSha256=(Get-FileHash -LiteralPath $Cli).Hash;cases=@();limitations=@('Owned small WPF source fixture compiled by the model-selected command; not a corporate project qualification.','Compatible provider function tools, not live direct OpenAI native input.','One explicit lifecycle attempt per case; only configured bounded HTTP response retries are permitted.','Model turn and decision artifacts establish host dispatch observations, not a separate wire-level provider audit.')}
function Write-Json($Value,[string]$Path){$Value|ConvertTo-Json -Depth 80|Set-Content -LiteralPath $Path -Encoding utf8}
function Save {Write-Json $report (Join-Path $root 'lifecycle-live-report.json')}
function Check([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Read-Json([string]$Path){Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json}
function Invoke-Cli([string[]]$Arguments,[string]$Output){
 if([IO.Path]::GetExtension($Cli) -eq '.dll'){& $Dotnet $Cli @Arguments 1> $Output 2> ($Output+'.stderr')}else{& $Cli @Arguments 1> $Output 2> ($Output+'.stderr')}
 return $LASTEXITCODE
}
function Start-Cli([string[]]$Arguments,[string]$Output){
 $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=if([IO.Path]::GetExtension($Cli) -eq '.dll'){$Dotnet}else{$Cli}
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 if([IO.Path]::GetExtension($Cli) -eq '.dll'){$info.ArgumentList.Add($Cli)}
 foreach($a in $Arguments){$info.ArgumentList.Add($a)}
 $process=[Diagnostics.Process]::Start($info)
 return @{Process=$process;Stdout=$process.StandardOutput.ReadToEndAsync();Stderr=$process.StandardError.ReadToEndAsync();Output=$Output}
}
function Finish-Cli($Running,[int]$TimeoutSeconds=600){
 if(-not $Running.Process.WaitForExit($TimeoutSeconds*1000)){throw 'Qualification CLI exceeded its bounded wait; original attempt is retained.'}
 [IO.File]::WriteAllText($Running.Output,$Running.Stdout.GetAwaiter().GetResult())
 [IO.File]::WriteAllText($Running.Output+'.stderr',$Running.Stderr.GetAwaiter().GetResult())
 return $Running.Process.ExitCode
}
function Prepare-Case([string]$Name,[string]$Settings,[string]$Mode){
 $dir=Join-Path $root $Name;$project=Join-Path $dir 'project';$null=[IO.Directory]::CreateDirectory($project)
 $stamp='Built for '+$Name
 @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net9.0-windows</TargetFramework><UseWPF>true</UseWPF><AssemblyName>Testy.LifecycleFixture</AssemblyName><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><Version>0.6.0</Version></PropertyGroup></Project>
'@|Set-Content -LiteralPath (Join-Path $project 'LifecycleFixture.csproj')
 $source=@'
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
public static class Program {
 [STAThread] public static void Main() {
  var panel=new StackPanel {Margin=new Thickness(28)};
  var title=new TextBlock {Text="Lifecycle build verification", FontSize=25, Margin=new Thickness(0,0,0,16)};
  var stamp=new TextBlock {Text="__STAMP__", FontSize=18, Margin=new Thickness(0,0,0,14)}; AutomationProperties.SetAutomationId(stamp,"BuildStamp");
  var status=new TextBlock {Text="Ready", FontSize=20, Margin=new Thickness(0,18,0,0)};AutomationProperties.SetAutomationId(status,"Status");
  var verify=new Button {Content="Verify prepared build", Width=230, Height=44, HorizontalAlignment=HorizontalAlignment.Left};AutomationProperties.SetAutomationId(verify,"Verify");
  verify.Click+=(_,_)=>status.Text="Prepared build verified";
  panel.Children.Add(title);panel.Children.Add(stamp);panel.Children.Add(verify);panel.Children.Add(status);
  new Application().Run(new Window {Title="Testy Lifecycle Qualification",Width=620,Height=340,Content=panel});
 }
}
'@
 $source.Replace('__STAMP__',$stamp)|Set-Content -LiteralPath (Join-Path $project 'Program.cs')
 if($Mode -eq 'failure'){'This deliberate syntax error prevents compilation.'|Add-Content -LiteralPath (Join-Path $project 'Program.cs')}
 $exe=Join-Path $project 'app/Testy.LifecycleFixture.exe'
 Check (-not(Test-Path -LiteralPath $exe)) 'The intended missing build output already exists.'
 $command=[ordered]@{id='build';description='Compile the owned WPF source fixture into app/Testy.LifecycleFixture.exe; no target is running yet.';executable=$Dotnet;arguments=@('build','LifecycleFixture.csproj','-c','Release','-o','app','--nologo');workingDirectory='.';timeoutSeconds=180}
 if($Mode -eq 'cancel'){
  @'
$ErrorActionPreference='Stop'
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'preparation-command-started.txt'),'owned fixed preparation command started')
Start-Sleep -Seconds 120
exit 0
'@|Set-Content -LiteralPath (Join-Path $project 'prepare-wait.ps1')
  $command.description='Run the owned cancellation fixture; it writes a start marker and waits. Do not invent or retry any command.'
  $command.executable=(Get-Process -Id $PID).Path;$command.arguments=@('-NoProfile','-NonInteractive','-File',(Join-Path $project 'prepare-wait.ps1'));$command.timeoutSeconds=150
 }
 $config=@{enabled=$true;rootDirectory=$project;commands=@($command);requiredBeforeUiCommands=@('build');maximumCalls=12;maximumCommandInvocations=1;maximumPlanningTurns=12}
 Write-Json $config (Join-Path $dir 'project.json')
 $settingsObject=Read-Json $Settings
 $settingsObject|Add-Member -NotePropertyName maximumProviderRetries -NotePropertyValue 2 -Force
 $settingsObject|Add-Member -NotePropertyName providerRetryDelayMs -NotePropertyValue 500 -Force
 $settingsObject.maximumAgentTurns=18
 Write-Json $settingsObject (Join-Path $dir 'provider.json')
 function Step([string]$Id,[string]$Action,[string]$Selector,[string]$Value){@{id=$Id;title=$Id;action=$Action;selector=$Selector;value=$Value;timeoutMs=5000;x=0;y=0;selectorAlternatives=@()}}
 $test=@{id='lifecycle-'+$Name;name='Prepared build '+$Name;intent='Execute every saved step exactly in order. The application was built and launched by the preceding preparation phase; do not run another command. Use immutable saved-step dispatch and finish after all three steps.';steps=@((Step 'verify-build-stamp' 'assertText' 'id:BuildStamp' $stamp),(Step 'click-verify' 'click' 'id:Verify' ''),(Step 'verify-result' 'assertText' 'id:Status' 'Prepared build verified'))}
 Write-Json $test (Join-Path $dir 'test.json')
 $profile=@{schema='testy.lifecycle.v1';id='profile-'+$Name;name='Prelaunch '+$Name;testFile='test.json';settingsFile='provider.json';projectFile='project.json';executable=$exe;probe=$false;replay=$false;maximumPreparationTurns=10;preparationTimeoutSeconds=300;workerTimeoutSeconds=300;startupTimeoutSeconds=20;shutdownGraceSeconds=3;preparationInstructions='Read Program.cs with project_read_file to inspect the actual source, then explicitly invoke the configured build command exactly once. Do not edit source or invent shell commands. Observe the actual command result. Complete preparation only if the required command succeeds; on failure explain the observed failure without retry. The host launches the built app only after successful preparation.'}
 if($Mode -eq 'cancel'){$profile.preparationInstructions='Read prepare-wait.ps1 with project_read_file, then explicitly invoke the configured build command once. This is an owned cancellation qualification. Never retry it or invent other commands; the host will request cancellation while it is active.'}
 Write-Json $profile (Join-Path $dir 'profile.json')
 Write-Json @{executable=$exe;existedBeforePreparation=$false;checkedAt=[datetimeoffset]::UtcNow;sourceSha256=(Get-FileHash -LiteralPath (Join-Path $project 'Program.cs')).Hash} (Join-Path $dir 'prelaunch-baseline.json')
 return @{Directory=$dir;Project=$project;Profile=(Join-Path $dir 'profile.json');Executable=$exe;Test=$test;Model=$settingsObject.model}
}
function Validate-Preparation($Lifecycle,[bool]$ExpectedPass){
 Check ($null -ne $Lifecycle.finishedAt) 'Lifecycle lacks a terminal timestamp.'
 Check ($null -ne $Lifecycle.preparation) 'Lifecycle lacks preparation evidence.'
 $prep=$Lifecycle.preparation;$records=@($prep.projectEvidence)
 Check (@($records|Where-Object {$_.command -eq $null -and $_.status -eq 'succeeded'}).Count -gt 0) 'No successful model-selected source inspection.'
 $commands=@($records|Where-Object {$_.command -ne $null});Check ($commands.Count -eq 1) 'Expected exactly one model-selected preparation command.'
 foreach($record in $records){
  Check ($null -ne $record.finishedAt -and (Test-Path -LiteralPath $record.evidencePath)) 'Preparation evidence is not terminal/durable.'
  Check ((Get-FileHash -LiteralPath $record.evidencePath).Hash -ceq (Get-Content -LiteralPath ($record.evidencePath+'.sha256') -Raw).Trim()) 'Preparation evidence sidecar hash mismatch.'
 }
 $command=$commands[0]
 Check ($command.request.commandId -ceq 'build' -and $command.command.processId -gt 0 -and $command.command.cleanupComplete) 'Command identity, real process or cleanup evidence is invalid.'
 Check ([datetimeoffset]$command.command.startedAt -ge [datetimeoffset]$command.startedAt -and [datetimeoffset]$command.command.finishedAt -le [datetimeoffset]$command.finishedAt) 'Command timing is outside its actual tool call.'
 if($ExpectedPass){Check ($prep.passed -and $command.status -eq 'succeeded' -and $command.command.exitCode -eq 0) 'Preparation was not successful.'}
 return $command
}
function Validate-Passed($Lifecycle,$Case){
 Check ($Lifecycle.passed -and $Lifecycle.cleanupComplete -and -not $Lifecycle.actionOutcomeUnknown) ('Lifecycle failed: '+$Lifecycle.message)
 $command=Validate-Preparation $Lifecycle $true
 Check (Test-Path -LiteralPath $Case.Executable) 'Model-selected build did not produce the executable.'
 $worker=Read-Json (Join-Path $Lifecycle.workerArtifactDirectory 'worker-result.json')
 Check ($worker.passed -and $worker.canonicalCoverageVerified -and $worker.cleanupComplete -and -not $worker.actionOutcomeUnknown) 'Worker did not verify canonical UI acceptance and cleanup.'
 Check ([datetimeoffset]$worker.targetStartedAt -gt [datetimeoffset]$command.finishedAt) 'Target started before preparation command completed.'
 Check ($worker.targetSha256 -ceq (Get-FileHash -LiteralPath $Case.Executable).Hash -and $worker.targetBuildSha256 -ceq $worker.targetBuildAfterSha256) 'Prepared target build provenance differs from the executed build.'
 $run=Read-Json (Join-Path $worker.runDirectory 'run.json')
 Check (@($run.projectEvidence|Where-Object {$_.command}).Count -eq 0) 'Preparation command was repeated after attach.'
 $saved=@($run.steps|Where-Object {$_.step.id -cin @($Case.Test.steps.id)})
 Check ($saved.Count -eq 3) 'Saved workflow coverage count differs.'
 for($i=0;$i -lt 3;$i++){foreach($field in @('id','action','selector','value','timeoutMs','x','y')){Check ($saved[$i].step.$field -ceq $Case.Test.steps[$i].$field) "Canonical saved field differs: $field"}}
 $images=0
 foreach($step in $run.steps){Check ($step.status -eq 'passed' -and $step.snapshot.target.processId -eq $worker.targetProcessId -and -not $step.snapshot.isTruncated) 'Executed step lacks complete owned-target observation.';Check (Get-FixtureScreenshotContent -Path $step.screenshotPath).passed 'Executed step PNG is unreadable or blank.';$images++}
 foreach($file in @('report.html','junit.xml','run.json')){Check (Test-Path -LiteralPath (Join-Path $worker.runDirectory $file)) "Missing UI report: $file"}
 return @{worker=$worker;images=$images;preparationTurns=$Lifecycle.preparation.modelTurns}
}
function Run-Case([string]$Name,[string]$Settings,[string]$Mode){
 $case=Prepare-Case $Name $Settings $Mode
 $entry=[ordered]@{name=$Name;mode=$Mode;model=$case.Model;startedAt=[datetimeoffset]::UtcNow;finishedAt=$null;passed=$false;artifactDirectory=$case.Directory;exitCode=$null;lifecycleDirectory=$null;preparationTurns=0;uiModelTurns=0;savedSteps=0;evidenceImages=0;error=$null}
 $running=$null
 try {
  if($Mode -in @('queue','cancel')){
   $workspace=Join-Path $case.Directory 'workspace'
   $enqueue=Join-Path $case.Directory 'enqueue.json';$exit=Invoke-Cli @('operations','--operation','enqueue','--workspace',$workspace,'--profile',$case.Profile) $enqueue
   Check ($exit -eq 0) 'Queue admission failed.';$job=Read-Json $enqueue
   $console=Join-Path $case.Directory 'pump.json';$running=Start-Cli @('operations','--operation','pump','--workspace',$workspace,'--once','--seconds','600') $console
   if($Mode -eq 'cancel'){
    $timer=[Diagnostics.Stopwatch]::StartNew();$marker=Join-Path $case.Project 'preparation-command-started.txt'
    while(-not(Test-Path -LiteralPath $marker)){Check (-not $running.Process.HasExited -and $timer.Elapsed.TotalSeconds -lt 150) 'Preparation command never reached the cancellation marker.';Start-Sleep -Milliseconds 100}
    $exit=Invoke-Cli @('operations','--operation','cancel','--workspace',$workspace,'--id',$job.id) (Join-Path $case.Directory 'cancellation.json');Check ($exit -eq 0) 'Durable queue cancellation request failed.'
   }
   $entry.exitCode=Finish-Cli $running
   $pump=Read-Json $console;Check (@($pump.results).Count -eq 1 -and $pump.results[0].job.id -ceq $job.id) 'Pump did not report exactly the frozen queued job.'
   $job=$pump.results[0].job;$lifecycle=$job.result
   Check ($null -ne $lifecycle) 'Queue has no terminal lifecycle result.'
  }else{
   $console=Join-Path $case.Directory 'console.json';$entry.exitCode=Invoke-Cli @('lifecycle-run','--profile',$case.Profile,'--artifacts',(Join-Path $case.Directory 'attempts')) $console
   $lifecycle=Read-Json $console
  }
  $entry.lifecycleDirectory=$lifecycle.artifactDirectory;$entry.preparationTurns=$lifecycle.preparation.modelTurns
  if($Mode -in @('direct','queue')){
   Check ($entry.exitCode -eq 0) 'Successful lifecycle returned a failing exit code.'
   $validated=Validate-Passed $lifecycle $case;$entry.uiModelTurns=$validated.worker.modelTurns;$entry.savedSteps=3;$entry.evidenceImages=$validated.images
  }else{
   Check ($entry.exitCode -eq 1 -and -not $lifecycle.passed -and $lifecycle.cleanupComplete -and -not $lifecycle.actionOutcomeUnknown) 'Negative lifecycle did not terminate with known cleanup and failure.'
   Check ([string]::IsNullOrEmpty($lifecycle.workerArtifactDirectory) -and -not(Test-Path -LiteralPath $case.Executable) -and @($lifecycle.checkpoints|Where-Object {$_.stage -eq 'workerStarting'}).Count -eq 0) 'Target/worker stage started after failed or cancelled preparation.'
   $command=Validate-Preparation $lifecycle $false
   if($Mode -eq 'failure'){Check ($lifecycle.status -eq 'failed' -and $command.command.exitCode -ne 0 -and $command.blocksFurtherActions) 'Expected compiler failure evidence was not retained.'}
   else{Check ($lifecycle.status -eq 'cancelled' -and $job.status -eq 'cancelled' -and $command.status -eq 'cancelled' -and $command.blocksFurtherActions) 'Active preparation command cancellation was not established.'}
  }
  $inspect=Join-Path $case.Directory 'inspection.json';$inspectExit=Invoke-Cli @('lifecycle-inspect','--directory',$lifecycle.artifactDirectory) $inspect
  $inspection=Read-Json $inspect
  Check ($inspection.status -ceq $lifecycle.status -and $inspection.id -ceq $lifecycle.id -and $inspectExit -eq $lifecycle.exitCode) 'Inspection changed the terminal lifecycle outcome.'
  $entry.passed=$true
 }catch{$entry.error=$_.Exception.Message}
 finally{
  if($running){if(-not $running.Process.HasExited){$running.Process.Kill($true);$running.Process.WaitForExit()};$running.Process.Dispose()}
  $entry.finishedAt=[datetimeoffset]::UtcNow;$report.cases+=[pscustomobject]$entry;Save
 }
}
function Connection-Case([string]$Name,[string]$Settings){
 $dir=Join-Path $root $Name;$null=[IO.Directory]::CreateDirectory($dir)
 $entry=[ordered]@{name=$Name;mode='connection-check';startedAt=[datetimeoffset]::UtcNow;finishedAt=$null;passed=$false;artifactDirectory=$dir;exitCode=$null;error=$null}
 try{
  $output=Join-Path $dir 'connection.json';$entry.exitCode=Invoke-Cli @('check-provider','--settings',$Settings,'--artifacts',$dir) $output
  $checked=Read-Json $output
  Check ($entry.exitCode -eq 0 -and $checked.passed -and -not $checked.nativeInputQualified) ('Structured provider connection check did not pass: '+$checked.message)
  $entry.passed=$true
 }catch{$entry.error=$_.Exception.Message}
 $entry.finishedAt=[datetimeoffset]::UtcNow;$report.cases+=[pscustomobject]$entry;Save
}
Save
Connection-Case 'terra-connection' $TerraSettings
Connection-Case 'gemini-connection' $GeminiSettings
Run-Case 'terra-direct-build' $TerraSettings 'direct'
Run-Case 'gemini-queued-build' $GeminiSettings 'queue'
Run-Case 'terra-compiler-failure' $TerraSettings 'failure'
Run-Case 'gemini-active-preparation-cancel' $GeminiSettings 'cancel'
$report.finishedAt=[datetimeoffset]::UtcNow;$report.passed=$report.cases.Count -eq 6 -and @($report.cases|Where-Object {-not $_.passed}).Count -eq 0;Save
$report|ConvertTo-Json -Depth 20
if(-not $report.passed){exit 1}

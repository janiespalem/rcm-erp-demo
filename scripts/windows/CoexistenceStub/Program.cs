using Velopack;

VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
var marker = Environment.GetEnvironmentVariable("FACTORYFLOW_COEXISTENCE_MARKER");
if (marker is not null) File.WriteAllText(marker, Environment.ProcessId.ToString());
Thread.Sleep(Timeout.Infinite);

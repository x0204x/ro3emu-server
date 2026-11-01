using Ro3Emu.Server;

// Offline restore of a backup (the server must be stopped): Ro3Emu.Server --restore-backup <file> [config args]
if (args is ["--restore-backup", var backup, .. var rest])
    return Ro3Emu.Server.Accounts.DatabaseRestore.RunCli(backup, ServerHost.ResolveOptions(rest), Console.Out);

// The previous version's files back, the server stopped (docs/guide/panel.md, Updates): Ro3Emu.Server --rollback-update [config args]
if (args is ["--rollback-update", .. var options])
    return Ro3Emu.Server.Updates.UpdateCli.Rollback(ServerHost.ResolveOptions(options), Console.Out);

// A restore the web panel planned runs here, before the realm opens (docs/guide/panel.md, Server control).
Ro3Emu.Server.Accounts.DatabaseRestore.RunRequested(ServerHost.ResolveOptions(args), Console.Out);
ServerHost.Create(args).Run();
// 0, or PanelOperations.RestartExitCode after a restart the panel planned.
return Environment.ExitCode;

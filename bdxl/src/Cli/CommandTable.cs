namespace Bdxl;

/// <summary>
/// Every command in one place: used for dispatch, "bdxl help" and "bdxl &lt;command&gt; --help".
/// The order here is the order of 'bdxl help' (groups appear where their first command is).
/// </summary>
public static class CommandTable
{
    public static readonly IReadOnlyList<ICommand> All =
    [
        // indexing and planning
        new InitCommand(), new ScanCommand(), new CompressionCommand(), new CopiesCommand(), new PlanCommand(), new BlanksCommand(), new IgnoreCommand(),
        // making discs
        new StageCommand(), new BurnedCommand(), new VerifyCommand(), new DiscardCommand(),
        // existing discs
        new ScanDiscCommand(),
        // looking things up
        new StatusCommand(), new FindCommand(), new DiscsCommand(), new CostCommand(),
        // recovery
        new LostCommand(), new DestroyedCommand(), new FoundCommand(), new RestageCommand(), new RestoreCommand(),
        // the catalog itself
        new CatalogCommand(), new PasswordCommand(), new RecoveryKeyCommand(), new UnlockCommand(), new LockCommand(), new AgentCommand(), new PurgeCommand(), new AliasCommand(), new SettingsCommand(),
        // help
        new WorkflowCommand(), new HelpCommand(),
    ];

    public static ICommand? Find(string name) =>
        All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

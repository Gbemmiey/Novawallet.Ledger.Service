namespace NovaWallet.Infrastructure.Options
{
    /// <summary>
    /// Strongly-typed configuration for application-startup behavior that must stay
    /// independent of <c>IHostEnvironment.IsDevelopment()</c> — most notably, whether the
    /// running process applies pending EF Core migrations itself.
    /// </summary>
    /// <remarks>
    /// Bound from the "Startup" section in configuration files (e.g. appsettings.json):
    /// <code>
    /// {
    ///   "Startup": {
    ///     "ApplyMigrationsOnStartup": false
    ///   }
    /// }
    /// </code>
    /// Previously, <c>Program.cs</c> ran <c>app.ApplyDatabaseMigrationsAsync()</c> only when
    /// <c>ASPNETCORE_ENVIRONMENT=Development</c> — which meant the only way to get automatic
    /// migrations in a container was to also opt every other <c>IsDevelopment()</c>-gated
    /// behavior (e.g. Serilog's console sink) into "dev mode," for a reason unrelated to
    /// logging or diagnostics. This flag decouples the two: migrations are now an explicit,
    /// environment-agnostic decision.
    /// </remarks>
    public class StartupOptions
    {
        /// <summary>
        /// Gets or sets whether the running process applies pending EF Core migrations to the
        /// database on boot, regardless of <c>ASPNETCORE_ENVIRONMENT</c>. Defaults to
        /// <see langword="false"/>: in the Docker Compose stack, the dedicated one-shot
        /// <c>migrator</c> service (see <c>docker-compose.yml</c>, and <c>Program.cs</c>'s
        /// <c>--migrate-only</c> mode) is what actually applies migrations, as an isolated,
        /// observable step that runs and exits before <c>api</c> starts — so <c>api</c> itself
        /// does not need this flag enabled in that setup. It exists for scenarios outside
        /// Compose (e.g. <c>dotnet run</c> against a fresh local database) where running the
        /// one-shot mode separately isn't convenient. <c>appsettings.Development.json</c> sets
        /// this to <see langword="true"/> specifically to preserve that exact convenience for
        /// local `dotnet run`/Visual Studio F5 debugging (launchSettings.json's profiles all set
        /// <c>ASPNETCORE_ENVIRONMENT=Development</c>), matching the auto-migration behavior that
        /// existed before this flag replaced the old <c>IsDevelopment()</c> check.
        /// </summary>
        public bool ApplyMigrationsOnStartup { get; set; } = false;
    }
}

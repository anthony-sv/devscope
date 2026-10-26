using System.Collections.ObjectModel;
using System.Data.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Database;

namespace Devscope.App.ViewModels;

/// <summary>Edits copies of the saved database connections; nothing is applied until Save.</summary>
public sealed partial class ConnectionManagerViewModel : ObservableObject
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly IDatabaseProvider _provider;
    private readonly ConnectionPasswords _passwords;

    public ConnectionManagerViewModel(
        IReadOnlyList<DbConnectionProfile> connections, Guid? selectedId, string? message, IDatabaseProvider provider, ConnectionPasswords passwords)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _provider = provider;
        _passwords = passwords;
        _message = message;
        Connections = [.. connections.Select(c => new ConnectionItemViewModel(c.Clone(), passwords.Get(c) ?? string.Empty))];
        _selectedConnection = Connections.FirstOrDefault(c => c.Id == selectedId) ?? Connections.FirstOrDefault();
    }

    public ObservableCollection<ConnectionItemViewModel> Connections { get; }

    public static IReadOnlyList<DbAuthentication> AuthenticationModes { get; } = [DbAuthentication.Windows, DbAuthentication.SqlLogin];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand), nameof(RemoveCommand), nameof(TestCommand))]
    private ConnectionItemViewModel? _selectedConnection;

    /// <summary>Why the window opened, e.g. a password is needed.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _validationError;

    /// <summary>A pasted connection string to turn into a connection.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _connectionStringText = string.Empty;

    public bool HasSelection => SelectedConnection is not null;

    /// <summary>Raised when the dialog should close; true means saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Add()
    {
        var item = new ConnectionItemViewModel(new DbConnectionProfile { Name = $"Connection {Connections.Count + 1}" }, string.Empty);
        Connections.Add(item);
        SelectedConnection = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        // Environments usually differ only in server or database: copy, then change that.
        var source = SelectedConnection!;
        var copy = source.ToProfile();
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";
        var item = new ConnectionItemViewModel(copy, source.Password) { SavePassword = source.SavePassword };
        Connections.Insert(Connections.IndexOf(source) + 1, item);
        SelectedConnection = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var index = Connections.IndexOf(SelectedConnection!);
        Connections.RemoveAt(index);
        SelectedConnection = Connections.Count == 0 ? null : Connections[Math.Min(index, Connections.Count - 1)];
    }

    private bool CanImport() => !string.IsNullOrWhiteSpace(ConnectionStringText);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        try
        {
            var profile = _provider.ParseConnectionString(ConnectionStringText.Trim(), out var password);
            var item = new ConnectionItemViewModel(profile, password ?? string.Empty);
            Connections.Add(item);
            SelectedConnection = item;
            ConnectionStringText = string.Empty;
            ValidationError = null;
        }
        catch (ArgumentException ex)
        {
            ValidationError = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestAsync()
    {
        var item = SelectedConnection!;
        item.TestStatus = "Connecting…";
        using var timeout = new CancellationTokenSource(TestTimeout);
        try
        {
            var info = await _provider.Open(item.ToProfile(), item.Password).TestAsync(timeout.Token);
            item.TestStatus = $"✓ Connected to {info.Server}: SQL Server {info.ProductVersion} ({info.Edition}), "
                + $"database {info.Database}, compatibility level {info.CompatibilityLevel}, collation {info.Collation}";
        }
        catch (OperationCanceledException)
        {
            item.TestStatus = $"✗ No answer within {TestTimeout.TotalSeconds:N0} seconds";
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            item.TestStatus = "✗ " + ex.Message;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (Connections.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Name)) is { } unnamed)
        {
            SelectedConnection = unnamed;
            ValidationError = "Every connection needs a name.";
            return;
        }

        if (Connections.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            ValidationError = $"Connection names must be unique: '{duplicate.Key}' is used twice.";
            return;
        }

        if (Connections.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Server)) is { } noServer)
        {
            SelectedConnection = noServer;
            ValidationError = $"'{noServer.Name}' needs a server.";
            return;
        }

        if (Connections.FirstOrDefault(c => c.IsSqlLogin && string.IsNullOrWhiteSpace(c.UserName)) is { } noUser)
        {
            SelectedConnection = noUser;
            ValidationError = $"'{noUser.Name}' uses a SQL Server login and needs a user name.";
            return;
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>The edited connections, with saved passwords encrypted; passwords not saved are kept for this session.</summary>
    public IReadOnlyList<DbConnectionProfile> Apply()
    {
        var result = new List<DbConnectionProfile>();
        foreach (var item in Connections)
        {
            var profile = item.ToProfile();
            var password = item.IsSqlLogin ? item.Password : string.Empty;
            profile.ProtectedPassword = item.SavePassword && password.Length > 0 ? _passwords.Protect(password) : string.Empty;
            _passwords.Remember(profile.Id, password);
            result.Add(profile);
        }

        return result;
    }
}

/// <summary>One connection being edited.</summary>
public sealed partial class ConnectionItemViewModel : ObservableObject
{
    private readonly Guid _id;

    public ConnectionItemViewModel(DbConnectionProfile profile, string password)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _id = profile.Id;
        _name = profile.Name;
        _server = profile.Server;
        _database = profile.Database;
        _authentication = profile.Authentication;
        _userName = profile.UserName;
        _password = password;
        _savePassword = profile.ProtectedPassword.Length > 0 || password.Length == 0;
        _encrypt = profile.Encrypt;
        _trustServerCertificate = profile.TrustServerCertificate;
        _readOnlyIntent = profile.ReadOnlyIntent;
        _commandTimeoutSeconds = profile.CommandTimeoutSeconds;
        _additionalOptions = profile.AdditionalOptions;
    }

    public Guid Id => _id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Target))]
    private string _server;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Target))]
    private string _database;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlLogin))]
    private DbAuthentication _authentication;

    [ObservableProperty]
    private string _userName;

    /// <summary>Plain text, in memory only; saved encrypted when <see cref="SavePassword"/> is on.</summary>
    [ObservableProperty]
    private string _password;

    [ObservableProperty]
    private bool _savePassword;

    [ObservableProperty]
    private bool _encrypt;

    [ObservableProperty]
    private bool _trustServerCertificate;

    [ObservableProperty]
    private bool _readOnlyIntent;

    [ObservableProperty]
    private int _commandTimeoutSeconds;

    [ObservableProperty]
    private string _additionalOptions;

    [ObservableProperty]
    private string? _testStatus;

    public bool IsSqlLogin => Authentication == DbAuthentication.SqlLogin;

    public string Target => string.IsNullOrWhiteSpace(Database) ? Server : $"{Server}/{Database}";

    public DbConnectionProfile ToProfile() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Server = Server.Trim(),
        Database = Database.Trim(),
        Authentication = Authentication,
        UserName = UserName.Trim(),
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
        ReadOnlyIntent = ReadOnlyIntent,
        CommandTimeoutSeconds = Math.Clamp(CommandTimeoutSeconds, 5, 3600),
        AdditionalOptions = AdditionalOptions.Trim(),
    };
}

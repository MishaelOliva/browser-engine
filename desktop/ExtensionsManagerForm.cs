namespace MishaWeb;

internal enum ExtensionManagerAction
{
    Open,
    ToggleEnabled,
    Remove
}

internal sealed record ExtensionManagerRow(
    string Id,
    string Name,
    string Version,
    bool IsEnabled,
    string? LaunchPath,
    bool CanUpdate,
    object Token);

internal sealed class ExtensionsManagerForm : Form
{
    private readonly Func<Task<IReadOnlyList<ExtensionManagerRow>>> rowProvider;
    private readonly Func<string, CancellationToken, Task> installFromStore;
    private readonly Func<string, CancellationToken, Task> importPackage;
    private readonly Func<string, CancellationToken, Task> importUnpacked;
    private readonly Func<ExtensionManagerRow, CancellationToken, Task<string>> updateFromStore;
    private readonly Func<ExtensionManagerAction, ExtensionManagerRow, Task> rowAction;
    private readonly Action openManagedFolder;
    private readonly Action openUserScriptsFolder;
    private readonly TextBox storeAddressBox = new();
    private readonly Button installButton = new();
    private readonly ListView extensionList = new();
    private readonly Button openButton = new();
    private readonly Button toggleButton = new();
    private readonly Button removeButton = new();
    private readonly Button updateButton = new();
    private readonly Button refreshButton = new();
    private readonly Button packageButton = new();
    private readonly Button unpackedButton = new();
    private readonly Button managedFolderButton = new();
    private readonly Button userScriptsButton = new();
    private readonly Button closeButton = new();
    private readonly Label statusLabel = new();
    private CancellationTokenSource? operationCancellation;
    private bool busy;
    private bool closeWhenIdle;

    public ExtensionsManagerForm(
        Func<Task<IReadOnlyList<ExtensionManagerRow>>> rowProvider,
        Func<string, CancellationToken, Task> installFromStore,
        Func<string, CancellationToken, Task> importPackage,
        Func<string, CancellationToken, Task> importUnpacked,
        Func<ExtensionManagerRow, CancellationToken, Task<string>> updateFromStore,
        Func<ExtensionManagerAction, ExtensionManagerRow, Task> rowAction,
        Action openManagedFolder,
        Action openUserScriptsFolder)
    {
        this.rowProvider = rowProvider;
        this.installFromStore = installFromStore;
        this.importPackage = importPackage;
        this.importUnpacked = importUnpacked;
        this.updateFromStore = updateFromStore;
        this.rowAction = rowAction;
        this.openManagedFolder = openManagedFolder;
        this.openUserScriptsFolder = openUserScriptsFolder;

        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Extensions";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 510);
        MinimumSize = new Size(620, 430);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Browser extensions manager";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var explanation = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            Margin = new Padding(0, 0, 0, 10),
            Text = "Install from a Chrome Web Store listing address or extension ID. "
                + "Extensions can read and change webpages, so install only ones you trust."
        };
        explanation.AccessibleName = "Extension safety notice";
        root.Controls.Add(explanation, 0, 0);

        var storePanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        storePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        storePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        storeAddressBox.Dock = DockStyle.Fill;
        storeAddressBox.PlaceholderText = "Chrome Web Store URL or 32-character extension ID";
        storeAddressBox.AccessibleName = "Chrome Web Store extension address";
        ConfigureButton(installButton, "Install", "Download and install the Chrome Web Store extension");
        installButton.Margin = new Padding(8, 0, 0, 0);
        storePanel.Controls.Add(storeAddressBox, 0, 0);
        storePanel.Controls.Add(installButton, 1, 0);
        root.Controls.Add(storePanel, 0, 1);

        extensionList.Dock = DockStyle.Fill;
        extensionList.View = View.Details;
        extensionList.FullRowSelect = true;
        extensionList.HideSelection = false;
        extensionList.MultiSelect = false;
        extensionList.AccessibleName = "Installed extensions";
        extensionList.Columns.Add("State", 85);
        extensionList.Columns.Add("Name", 225);
        extensionList.Columns.Add("Version", 95);
        extensionList.Columns.Add("Extension ID", 305);
        root.Controls.Add(extensionList, 0, 2);

        var selectionActions = NativeDialogLayout.CreateActionBar(wrap: true);
        ConfigureButton(openButton, "Open", "Open the selected extension popup or options page");
        ConfigureButton(toggleButton, "Disable", "Enable or disable the selected extension");
        ConfigureButton(removeButton, "Remove", "Remove the selected extension and its managed files");
        ConfigureButton(updateButton, "Check update", "Check the Chrome Web Store for a signed update");
        ConfigureButton(refreshButton, "Refresh", "Refresh the installed extensions list");
        selectionActions.Controls.AddRange([refreshButton, removeButton, updateButton, toggleButton, openButton]);
        root.Controls.Add(selectionActions, 0, 3);

        var importActions = NativeDialogLayout.CreateActionBar(wrap: true);
        ConfigureButton(packageButton, "Install package…", "Install a local CRX or ZIP extension package");
        ConfigureButton(unpackedButton, "Load unpacked…", "Copy and install an unpacked extension folder");
        ConfigureButton(managedFolderButton, "Managed files", "Open MishaWeb's managed extension package folder");
        ConfigureButton(userScriptsButton, "User scripts", "Open the separate JavaScript user scripts folder");
        ConfigureButton(closeButton, "Close", "Close the extensions manager");
        CancelButton = closeButton;
        importActions.Controls.AddRange(
            [closeButton, userScriptsButton, managedFolderButton, unpackedButton, packageButton]);
        root.Controls.Add(importActions, 0, 4);

        statusLabel.AutoEllipsis = true;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.AutoSize = true;
        statusLabel.MinimumSize = new Size(0, 24);
        statusLabel.Margin = new Padding(0, 8, 0, 0);
        statusLabel.Text = "Loading installed extensions…";
        statusLabel.AccessibleName = "Extension manager status";
        root.Controls.Add(statusLabel, 0, 5);
        Controls.Add(root);
        NativeUiTheme.Apply(this, installButton);

        installButton.Click += async (_, _) => await InstallStoreExtensionAsync();
        storeAddressBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await InstallStoreExtensionAsync();
        };
        extensionList.SelectedIndexChanged += (_, _) => UpdateButtons();
        extensionList.DoubleClick += async (_, _) => await RunRowActionAsync(ExtensionManagerAction.Open);
        openButton.Click += async (_, _) => await RunRowActionAsync(ExtensionManagerAction.Open);
        toggleButton.Click += async (_, _) => await RunRowActionAsync(ExtensionManagerAction.ToggleEnabled);
        removeButton.Click += async (_, _) => await RemoveSelectedAsync();
        updateButton.Click += async (_, _) => await UpdateSelectedAsync();
        refreshButton.Click += async (_, _) => await RefreshRowsAsync();
        packageButton.Click += async (_, _) => await SelectPackageAsync();
        unpackedButton.Click += async (_, _) => await SelectUnpackedAsync();
        managedFolderButton.Click += (_, _) => openManagedFolder();
        userScriptsButton.Click += (_, _) => openUserScriptsFolder();
        closeButton.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (!busy || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            closeWhenIdle = true;
            operationCancellation?.Cancel();
            SetStatus("Finishing the current extension operation before closing…");
        };
        Shown += async (_, _) => await RefreshRowsAsync();
        UpdateButtons();
    }

    public void PrefillStoreAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(storeAddressBox.Text)
            && !string.IsNullOrWhiteSpace(value))
        {
            storeAddressBox.Text = value;
        }
    }

    public Task RefreshAfterExternalChangeAsync() => RefreshRowsAsync();

    private ExtensionManagerRow? SelectedRow =>
        extensionList.SelectedItems.Count == 1
            ? extensionList.SelectedItems[0].Tag as ExtensionManagerRow
            : null;

    private async Task InstallStoreExtensionAsync()
    {
        var value = storeAddressBox.Text.Trim();
        if (BrowserExtensions.ParseChromeWebStoreExtensionId(value) is null)
        {
            SetStatus("Paste a Chrome Web Store listing address or extension ID.");
            storeAddressBox.Focus();
            return;
        }
        await RunOperationAsync(
            "Downloading and installing extension…",
            token => installFromStore(value, token),
            "Extension installed");
    }

    private async Task SelectPackageAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Install extension package",
            Filter = "Chrome extension packages (*.crx;*.zip)|*.crx;*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync(
            "Importing extension package…",
            token => importPackage(picker.FileName, token),
            "Extension package installed");
    }

    private async Task SelectUnpackedAsync()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the folder containing the extension's manifest.json",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync(
            "Copying and installing unpacked extension…",
            token => importUnpacked(picker.SelectedPath, token),
            "Unpacked extension installed");
    }

    private async Task RemoveSelectedAsync()
    {
        var row = SelectedRow;
        if (row is null) return;
        var confirmation = MessageBox.Show(
            this,
            $"Remove {row.Name}? Its MishaWeb-managed package files will also be deleted.",
            "Remove extension",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;
        await RunRowActionAsync(ExtensionManagerAction.Remove);
    }

    private async Task UpdateSelectedAsync()
    {
        var row = SelectedRow;
        if (row is null || !row.CanUpdate || busy) return;
        var completed = "Extension update check completed";
        await RunOperationCoreAsync(
            "Checking the Chrome Web Store for a signed update\u2026",
            async token => completed = await updateFromStore(row, token),
            () => completed);
    }

    private async Task RunRowActionAsync(ExtensionManagerAction action)
    {
        var row = SelectedRow;
        if (row is null
            || busy
            || (action == ExtensionManagerAction.Open && (!row.IsEnabled || row.LaunchPath is null)))
        {
            return;
        }
        var progress = action switch
        {
            ExtensionManagerAction.Open => "Opening extension…",
            ExtensionManagerAction.ToggleEnabled => row.IsEnabled
                ? "Disabling extension…"
                : "Enabling extension…",
            ExtensionManagerAction.Remove => "Removing extension…",
            _ => "Updating extension…"
        };
        var completed = action switch
        {
            ExtensionManagerAction.Open => "Extension opened",
            ExtensionManagerAction.ToggleEnabled => row.IsEnabled
                ? "Extension disabled"
                : "Extension enabled",
            ExtensionManagerAction.Remove => "Extension removed",
            _ => "Extension updated"
        };
        await RunOperationAsync(progress, _ => rowAction(action, row), completed);
    }

    private async Task RunOperationAsync(
        string progress,
        Func<CancellationToken, Task> operation,
        string completed) =>
        await RunOperationCoreAsync(progress, operation, () => completed);

    private async Task RunOperationCoreAsync(
        string progress,
        Func<CancellationToken, Task> operation,
        Func<string> completed)
    {
        if (busy) return;
        busy = true;
        operationCancellation = new CancellationTokenSource();
        SetStatus(progress);
        UpdateButtons();
        try
        {
            var operationSucceeded = false;
            try
            {
                await operation(operationCancellation.Token);
                operationSucceeded = true;
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) SetStatus("Extension operation canceled");
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    SetStatus(error.Message);
                    MessageBox.Show(
                        this,
                        error.Message,
                        "Extension operation failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }

            if (operationSucceeded && !IsDisposed && !closeWhenIdle)
            {
                try
                {
                    await RefreshRowsCoreAsync();
                    if (!IsDisposed) SetStatus(completed());
                }
                catch (Exception error)
                {
                    if (!IsDisposed) SetStatus($"{completed()}; refresh failed: {error.Message}");
                }
            }
        }
        finally
        {
            operationCancellation.Dispose();
            operationCancellation = null;
            busy = false;
            if (!IsDisposed)
            {
                if (closeWhenIdle) Close();
                else UpdateButtons();
            }
        }
    }

    private async Task RefreshRowsAsync()
    {
        if (busy) return;
        busy = true;
        SetStatus("Refreshing installed extensions…");
        UpdateButtons();
        try
        {
            await RefreshRowsCoreAsync();
            if (IsDisposed) return;
            SetStatus(extensionList.Items.Count == 0
                ? "No browser extensions are installed"
                : $"{extensionList.Items.Count} extension(s) installed");
        }
        catch (Exception error)
        {
            if (!IsDisposed) SetStatus(error.Message);
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                if (closeWhenIdle) Close();
                else UpdateButtons();
            }
        }
    }

    private async Task RefreshRowsCoreAsync()
    {
        var selectedId = SelectedRow?.Id;
        var rows = await rowProvider();
        if (IsDisposed) return;
        extensionList.BeginUpdate();
        try
        {
            extensionList.Items.Clear();
            foreach (var row in rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ListViewItem(row.IsEnabled ? "Enabled" : "Disabled") { Tag = row };
                item.SubItems.Add(row.Name);
                item.SubItems.Add(row.Version);
                item.SubItems.Add(row.Id);
                extensionList.Items.Add(item);
                if (row.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
            }
        }
        finally
        {
            extensionList.EndUpdate();
        }
        if (extensionList.SelectedItems.Count == 0 && extensionList.Items.Count > 0)
        {
            extensionList.Items[0].Selected = true;
        }
    }

    private void UpdateButtons()
    {
        var row = SelectedRow;
        storeAddressBox.Enabled = !busy;
        installButton.Enabled = !busy;
        packageButton.Enabled = !busy;
        unpackedButton.Enabled = !busy;
        refreshButton.Enabled = !busy;
        extensionList.Enabled = !busy;
        openButton.Enabled = !busy && row?.LaunchPath is not null && row.IsEnabled;
        toggleButton.Enabled = !busy && row is not null;
        toggleButton.Text = row?.IsEnabled == false ? "Enable" : "Disable";
        toggleButton.AccessibleName = toggleButton.Text;
        toggleButton.AccessibleDescription = row?.IsEnabled == false
            ? "Enable the selected extension"
            : "Disable the selected extension";
        removeButton.Enabled = !busy && row is not null;
        updateButton.Enabled = !busy && row?.CanUpdate == true;
        managedFolderButton.Enabled = !busy;
        userScriptsButton.Enabled = !busy;
    }

    private void SetStatus(string text)
    {
        statusLabel.Text = text;
        statusLabel.AccessibleName = text;
    }

    private static void ConfigureButton(Button button, string text, string description)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(78, 30);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Margin = new Padding(4, 4, 0, 4);
        button.Text = text;
        button.AccessibleName = text;
        button.AccessibleDescription = description;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            operationCancellation?.Cancel();
            operationCancellation?.Dispose();
        }
        base.Dispose(disposing);
    }
}

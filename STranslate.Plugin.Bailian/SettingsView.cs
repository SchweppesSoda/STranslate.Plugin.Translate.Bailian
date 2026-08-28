using System.Windows;
using System.Windows.Controls;
using STranslate.Plugin;

namespace STranslate.Plugin.Bailian;

internal sealed class SettingsView : UserControl
{
    private readonly Settings _settings;
    private readonly Action _save;
    private readonly Grid _grid = new() { Margin = new Thickness(16) };
    private int _row;

    public SettingsView(IPluginContext context, Settings settings, Action save, Action editPrompts)
    {
        _settings = settings;
        _save = save;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddCombo("计费模式", settings.AccessMode,
            [
                new("按量付费", BillingMode.PayAsYouGo),
                new("Coding Plan", BillingMode.CodingPlan),
                new("Token Plan", BillingMode.TokenPlan)
            ], value => settings.AccessMode = value);
        AddCombo("地域", settings.Region,
            [new("中国（北京）", "china"), new("国际（新加坡）", "singapore")],
            value => settings.Region = value);
        AddText("Workspace ID", settings.WorkspaceId, value => settings.WorkspaceId = value);
        AddText("自定义按量 Base URL", settings.CustomBaseUrl, value => settings.CustomBaseUrl = value);
        AddPassword("API Key", settings.ApiKey, value => settings.ApiKey = value);
        AddEditableCombo("模型", settings.Model, BailianConfig.PresetModels, value => settings.Model = value);
        AddInteger("最大输出 Token", settings.MaxTokens, value => settings.MaxTokens = value);
        AddCheck("流式输出", settings.Stream, value => settings.Stream = value);
        AddCheck("启用思考", settings.EnableThinking, value => settings.EnableThinking = value);
        AddCombo("思考强度", settings.ReasoningEffort,
            [new("自动", "auto"), new("低", "low"), new("中", "medium"), new("高", "high")],
            value => settings.ReasoningEffort = value);
        AddCombo("OCR 分辨率", settings.OcrResolution,
            [new("自动", "auto"), new("快速", "fast"), new("高", "high")],
            value => settings.OcrResolution = value);

        AddLabel("翻译提示词");
        var promptButton = new Button
        {
            Content = "编辑提示词…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 4, 12, 4)
        };
        promptButton.Click += (_, _) => editPrompts();
        AddControl(promptButton);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _grid
        };
    }

    private void AddCombo(string label, string current, IReadOnlyList<Choice> choices, Action<string> update)
    {
        AddLabel(label);
        var combo = new ComboBox { ItemsSource = choices, SelectedValuePath = nameof(Choice.Value), SelectedValue = current };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedValue is string value)
            {
                update(value);
                _save();
            }
        };
        AddControl(combo);
    }

    private void AddEditableCombo(string label, string current, IReadOnlyList<string> choices, Action<string> update)
    {
        AddLabel(label);
        var combo = new ComboBox { IsEditable = true, ItemsSource = choices, Text = current };
        combo.LostKeyboardFocus += (_, _) =>
        {
            update(combo.Text.Trim());
            _save();
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string value)
            {
                update(value);
                _save();
            }
        };
        AddControl(combo);
    }

    private void AddText(string label, string current, Action<string> update)
    {
        AddLabel(label);
        var box = new TextBox { Text = current };
        box.LostKeyboardFocus += (_, _) =>
        {
            update(box.Text.Trim());
            _save();
        };
        AddControl(box);
    }

    private void AddPassword(string label, string current, Action<string> update)
    {
        AddLabel(label);
        var box = new PasswordBox { Password = current };
        box.LostKeyboardFocus += (_, _) =>
        {
            update(box.Password.Trim());
            _save();
        };
        AddControl(box);
    }

    private void AddInteger(string label, int current, Action<int> update)
    {
        AddLabel(label);
        var box = new TextBox { Text = current.ToString() };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (int.TryParse(box.Text, out var value) && value > 0)
            {
                update(value);
                _save();
            }
            else
            {
                box.Text = current.ToString();
            }
        };
        AddControl(box);
    }

    private void AddCheck(string label, bool current, Action<bool> update)
    {
        AddLabel(label);
        var box = new CheckBox { IsChecked = current, VerticalAlignment = VerticalAlignment.Center };
        box.Click += (_, _) =>
        {
            update(box.IsChecked == true);
            _save();
        };
        AddControl(box);
    }

    private void AddLabel(string text)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 5, 12, 9),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(label, _row);
        Grid.SetColumn(label, 0);
        _grid.Children.Add(label);
    }

    private void AddControl(Control control)
    {
        control.Margin = new Thickness(0, 2, 0, 8);
        control.MinWidth = 260;
        Grid.SetRow(control, _row);
        Grid.SetColumn(control, 1);
        _grid.Children.Add(control);
        _row++;
    }

    private sealed record Choice(string Label, string Value)
    {
        public override string ToString() => Label;
    }
}

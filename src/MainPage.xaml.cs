using AiComparison.ViewModels;

namespace AiComparison;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
        
        UpdateTabButtonStyles(0);
        UpdateScenarioButtonStyles();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private void OnLocalTabClicked(object? sender, EventArgs e)
    {
        _viewModel.SelectedTabIndex = 0;
        UpdateTabButtonStyles(0);
    }

    private void OnCloudTabClicked(object? sender, EventArgs e)
    {
        _viewModel.SelectedTabIndex = 1;
        UpdateTabButtonStyles(1);
    }

    private void OnHybridTabClicked(object? sender, EventArgs e)
    {
        _viewModel.SelectedTabIndex = 2;
        UpdateTabButtonStyles(2);
    }

    private void OnFinalTabClicked(object? sender, EventArgs e)
    {
        if (!_viewModel.CanCompare) return;
        
        _viewModel.SelectedTabIndex = 3;
        UpdateTabButtonStyles(3);
    }

    private void OnPowerScenarioClicked(object? sender, EventArgs e)
    {
        _viewModel.SelectedScenario = ScenarioType.Power;
        UpdateScenarioButtonStyles();
        ClearResults();
    }

    private void OnPrivacyScenarioClicked(object? sender, EventArgs e)
    {
        _viewModel.SelectedScenario = ScenarioType.Privacy;
        UpdateScenarioButtonStyles();
        ClearResults();
    }

    private void ClearResults()
    {
        // Clear cached summaries and benchmarks when switching scenarios
        _viewModel.LocalSummary = string.Empty;
        _viewModel.CloudSummary = string.Empty;
        _viewModel.HybridSummary = string.Empty;
        _viewModel.OutputText = string.Empty;
        _viewModel.LocalBenchmark = Models.BenchmarkResult.Empty;
        _viewModel.CloudBenchmark = Models.BenchmarkResult.Empty;
        _viewModel.HybridBenchmark = Models.BenchmarkResult.Empty;
    }

    private void UpdateTabButtonStyles(int selectedIndex)
    {
        var tonalStyle = Application.Current!.Resources["M3TonalButton"] as Style;
        var textStyle = Application.Current!.Resources["M3TextButton"] as Style;

        LocalTabButton.Style = selectedIndex == 0 ? tonalStyle : textStyle;
        CloudTabButton.Style = selectedIndex == 1 ? tonalStyle : textStyle;
        HybridTabButton.Style = selectedIndex == 2 ? tonalStyle : textStyle;
        FinalTabButton.Style = selectedIndex == 3 ? tonalStyle : textStyle;
    }

    private void UpdateScenarioButtonStyles()
    {
        var tonalStyle = Application.Current!.Resources["M3TonalButton"] as Style;
        var textStyle = Application.Current!.Resources["M3TextButton"] as Style;

        PowerScenarioButton.Style = _viewModel.SelectedScenario == ScenarioType.Power ? tonalStyle : textStyle;
        PrivacyScenarioButton.Style = _viewModel.SelectedScenario == ScenarioType.Privacy ? tonalStyle : textStyle;
    }
}

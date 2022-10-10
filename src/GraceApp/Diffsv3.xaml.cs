namespace GraceApp;

public partial class Diffsv3 : ContentPage
{
    public Diffsv3()
    {
        InitializeComponent();
        BindingContext = referencesViewModel;
        
    }

    protected async override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        await referencesViewModel.LoadViewModel();
        base.OnNavigatedTo(args);
    }

    public ReferencesViewModel referencesViewModel = new();
}

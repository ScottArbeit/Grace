using CommunityToolkit.Mvvm.ComponentModel;
using DiffPlex.DiffBuilder.Model;
using static GraceApp.Services;
using static Grace.Shared.Dto.Diff;
using static Grace.Shared.Dto.Reference;
using static Grace.Shared.Types;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace GraceApp;

public partial class Diffs : ContentPage
{
	public Diffs()
	{
		InitializeComponent();
		BindingContext = referencesViewModel;
    }

	public ReferencesViewModel referencesViewModel = new();

	protected override async void OnNavigatedTo(NavigatedToEventArgs args)
	{
		base.OnNavigatedTo(args);
        await referencesViewModel.LoadViewModel();
    }
}

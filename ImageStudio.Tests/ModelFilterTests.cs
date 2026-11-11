using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Każdy selektor modeli ma własne filtry: ustawienie w jednej zakładce nie zmienia listy w drugiej
public sealed class ModelFilterTests
{
    [Fact]
    public void Filters_of_generation_and_edit_tabs_are_independent()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            var vm = app.Vm;
            vm.Models.Add(new ModelOption("a/flux", "a", false, ["text"], null));
            vm.EditModels.Add(new ModelOption("a/flux", "a", false, ["text"], null));

            vm.GenerateFilter.SearchText = "zzz";

            Assert.Empty(vm.ModelsView.Cast<object>());
            Assert.Single(vm.EditModelsView.Cast<object>());
            Assert.Equal("", vm.EditFilter.SearchText);
        });
    }
}

using System.Collections.ObjectModel;
using ConversationAssistant.Core.Models;
using Microsoft.UI.Xaml;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    public ObservableCollection<ProductGuideRow> ProductGuides { get; } = [];
    public Visibility EmptyProductGuidesVisibility => ProductGuides.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshProductGuides()
    {
        var products = _conversation.ProductGuides;
        var keys = products.Select(product => ProductGuide.IdentityKey(product.Name)).ToHashSet(StringComparer.Ordinal);
        for (var index = ProductGuides.Count - 1; index >= 0; index--)
            if (!keys.Contains(ProductGuide.IdentityKey(ProductGuides[index].Product.Name))) ProductGuides.RemoveAt(index);
        foreach (var product in products)
        {
            var row = ProductGuides.FirstOrDefault(item => ProductGuide.IdentityKey(item.Product.Name) == ProductGuide.IdentityKey(product.Name));
            if (row is null) ProductGuides.Add(new ProductGuideRow(product, Texts));
            else row.UpdateLanguage(Texts);
        }
        Notify(nameof(EmptyProductGuidesVisibility));
    }
}

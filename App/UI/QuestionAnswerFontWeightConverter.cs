using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Data;

namespace ConversationAssistant_App.UI;

public sealed class QuestionAnswerFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? FontWeights.Bold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

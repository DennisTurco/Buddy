using System.Windows;
using System.Windows.Controls;
using Buddy.Core.Chat;

namespace Buddy.App.Chat;

public sealed class EntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Assistant { get; set; }
    public DataTemplate? System { get; set; }
    public DataTemplate? Permission { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        PermissionEntry => Permission,
        ChatEntry { Speaker: Speaker.User } => User,
        ChatEntry { Speaker: Speaker.System } => System,
        _ => Assistant,
    };
}

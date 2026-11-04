
using EFT;

namespace MiyakoCarryService.Client.Extensions
{
    public static class StringExtensions
    {
        extension(string id)
        {
            public string McsLocalized()
            {
                // Fork: the fork's own strings (C2Sain/ForkText.cs) win.
                if (C2Sain.ForkText.TryGet(id, out var forkText))
                {
                    return forkText;
                }
                if (id.Localized() == id)
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        var value = LocalizationManager.Instance.LocalizedValue(id, MiyakoCarryServicePlugin.DefaultLang);
                        return string.IsNullOrEmpty(value) ? id : value;
                    }
                    return id;
                }
                else
                {
                    return id.Localized();
                }
            }
        }
    }
}

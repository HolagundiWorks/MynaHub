namespace Adms.Desktop;

/// <summary>
/// Services named in the Council of Architecture Conditions of Engagement and Scale of Charges.
/// Comprehensive, interior and landscape each have their own engagement.
/// Urban design, site development, conservation, retrofitting and signage are the further services on those pages.
/// </summary>
internal static class Consultancy
{
    public static readonly (string Key, string Label)[] Types =
    [
        ("comprehensive", "Comprehensive architectural services"),
        ("interior", "Interior architecture"),
        ("landscape", "Landscape architecture"),
        ("urban", "Urban design and urban renewal"),
        ("site", "Site development"),
        ("conservation", "Architectural conservation"),
        ("retrofitting", "Retrofitting of buildings"),
        ("signage", "Graphic design and signage"),
    ];

    public static string Label(string key)
    {
        foreach (var (id, label) in Types)
        {
            if (id == key)
                return label;
        }
        return Types[0].Label;
    }
}

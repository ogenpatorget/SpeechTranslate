using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace ChurchTranslate.Api;

// Keeps ASP.NET's background key ring in memory as well as the cookie/token provider.
public sealed class MemoryXmlRepository : IXmlRepository
{
    private readonly object gate = new();
    private readonly List<XElement> elements = [];

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (gate) return elements.Select(x => new XElement(x)).ToArray();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (gate) elements.Add(new XElement(element));
    }
}

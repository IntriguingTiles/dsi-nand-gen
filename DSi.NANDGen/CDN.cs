using System.Xml;

namespace DSi.NANDGen {
    public static class CDN {
        private static readonly HttpClient http = new();
        private static readonly string updateBaseURL = "http://nus.shop.wii.com";
        private static readonly string contentBaseURL = "http://ccs.t.shop.nintendowifi.net";

        public static async Task<TitleID[]> GetSystemUpdate(string region) {
            var titles = new List<TitleID>();

            using var req = new HttpRequestMessage {
                Method = HttpMethod.Post,
                RequestUri = new Uri($"{updateBaseURL}/nus/services/NetUpdateSOAP"),
                Headers = {
                    { "SOAPAction", "urn:nus.wsapi.broadon.com/GetSystemUpdate" }
                },
                Content = new StringContent($@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/""
  xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
  xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <soapenv:Body>
    <GetSystemUpdateRequest xmlns=""urn:nus.wsapi.broadon.com"">
      <Version>1.0</Version>
      <MessageId>1</MessageId>
      <DeviceId>12952162362</DeviceId>
      <RegionId>{region}</RegionId>
      <Attribute>2</Attribute>
    </GetSystemUpdateRequest>
  </soapenv:Body>
</soapenv:Envelope>")
            };

            using var res = await http.SendAsync(req);
            res.EnsureSuccessStatusCode();

            var xml = new XmlDocument();
            xml.LoadXml(await res.Content.ReadAsStringAsync());
            var nodes = xml.GetElementsByTagName("TitleVersion");

            for (int i = 0; i < nodes.Count; i++) {
                var tid = nodes[i]!["TitleId"]!.InnerText;
                var version = nodes[i]!["Version"]!.InnerText;
                titles.Add(new(tid, version));
            }

            return [.. titles];
        }

        public static async Task<(TMD, byte[])> DownloadTMD(TitleID title) {
            using var res = await http.GetAsync($"{contentBaseURL}/ccs/download/{title.Combined()}/tmd{(title.Version is not null ? $".{title.Version}" : "")}");
            res.EnsureSuccessStatusCode();
            var data = await res.Content.ReadAsByteArrayAsync();
            return (new(data), data);
        }

        public static async Task<(Ticket, byte[])> DownloadTicket(TitleID title) {
            using var res = await http.GetAsync($"{contentBaseURL}/ccs/download/{title.Combined()}/cetk");
            res.EnsureSuccessStatusCode();
            var data = await res.Content.ReadAsByteArrayAsync();
            return (new(data), data);
        }

        public static async Task<TitleContent> DownloadContent(TitleID title, TMD.Content content) {
            using var res = await http.GetAsync($"{contentBaseURL}/ccs/download/{title.Combined()}/{content.ID:x8}");
            res.EnsureSuccessStatusCode();
            var data = await res.Content.ReadAsByteArrayAsync();
            return new(data);
        }
    }
}

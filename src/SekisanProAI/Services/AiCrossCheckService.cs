using System.Net.Http;
using SekisanProAI.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SekisanProAI.Services;

public sealed class AiCrossCheckService
{
    private readonly SecretStore _secrets;
    private readonly HttpClient _http=new(){Timeout=TimeSpan.FromMinutes(3)};
    public AiCrossCheckService(SecretStore secrets)=>_secrets=secrets;

    public async Task<List<AiCheckResult>> CheckAsync(IEnumerable<EstimateLine> lines, RegionProfile region)
    {
        var sample=lines.Take(120).Select(x=>$"{x.No}. {x.Name} / {x.Spec} / {x.Quantity} {x.Unit} / {x.UnitPrice:N0}円 / {x.MatchStatus} / {x.Note}");
        var prompt=$"""
あなたは日本の公共工事積算の照合担当です。
Golden Riverから取得した単価は基準値であり、勝手に置き換えないでください。
対象地域: {region}
以下の積算行を確認し、名称・規格・単位・数量・地域条件・不自然な候補・重複・桁違いをチェックしてください。
価格を捏造せず、不明なものは「要確認」としてください。
回答は日本語で、重要度の高い順に短く箇条書きにしてください。

{string.Join("\n",sample)}
""";
        var tasks=new List<Task<AiCheckResult>>();
        if(!string.IsNullOrWhiteSpace(_secrets.Load("openai_key"))) tasks.Add(OpenAi(prompt));
        if(!string.IsNullOrWhiteSpace(_secrets.Load("anthropic_key"))) tasks.Add(Anthropic(prompt));
        if(!string.IsNullOrWhiteSpace(_secrets.Load("gemini_key"))) tasks.Add(Gemini(prompt));
        if(tasks.Count==0) return [new(){Provider="AI",Success=false,Error="APIキーが未設定です。AI設定から登録してください。"}];
        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<AiCheckResult> OpenAi(string prompt)
    {
        try
        {
            using var req=new HttpRequestMessage(HttpMethod.Post,"https://api.openai.com/v1/responses");
            req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_secrets.Load("openai_key"));
            var body=JsonSerializer.Serialize(new {model="gpt-5.6",input=prompt,store=false});
            req.Content=new StringContent(body,Encoding.UTF8,"application/json");
            using var res=await _http.SendAsync(req); var json=await res.Content.ReadAsStringAsync();
            if(!res.IsSuccessStatusCode) return new(){Provider="OpenAI",Success=false,Error=json};
            using var doc=JsonDocument.Parse(json);
            var texts=new List<string>();
            if(doc.RootElement.TryGetProperty("output",out var output))
                foreach(var item in output.EnumerateArray())
                    if(item.TryGetProperty("content",out var content))
                        foreach(var c in content.EnumerateArray())
                            if(c.TryGetProperty("text",out var t)) texts.Add(t.GetString()??"");
            return new(){Provider="OpenAI",Success=true,Result=string.Join("\n",texts)};
        }catch(Exception ex){return new(){Provider="OpenAI",Success=false,Error=ex.Message};}
    }

    private async Task<AiCheckResult> Anthropic(string prompt)
    {
        try
        {
            using var req=new HttpRequestMessage(HttpMethod.Post,"https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key",_secrets.Load("anthropic_key"));
            req.Headers.Add("anthropic-version","2023-06-01");
            var body=JsonSerializer.Serialize(new {model="claude-sonnet-4-6",max_tokens=1800,messages=new[]{new{role="user",content=prompt}}});
            req.Content=new StringContent(body,Encoding.UTF8,"application/json");
            using var res=await _http.SendAsync(req); var json=await res.Content.ReadAsStringAsync();
            if(!res.IsSuccessStatusCode) return new(){Provider="Claude",Success=false,Error=json};
            using var doc=JsonDocument.Parse(json);
            var texts=new List<string>();
            if(doc.RootElement.TryGetProperty("content",out var content))
                foreach(var c in content.EnumerateArray()) if(c.TryGetProperty("text",out var t)) texts.Add(t.GetString()??"");
            return new(){Provider="Claude",Success=true,Result=string.Join("\n",texts)};
        }catch(Exception ex){return new(){Provider="Claude",Success=false,Error=ex.Message};}
    }

    private async Task<AiCheckResult> Gemini(string prompt)
    {
        try
        {
            using var req=new HttpRequestMessage(HttpMethod.Post,"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent");
            req.Headers.Add("x-goog-api-key",_secrets.Load("gemini_key"));
            var body=JsonSerializer.Serialize(new {contents=new[]{new{parts=new[]{new{text=prompt}}}}});
            req.Content=new StringContent(body,Encoding.UTF8,"application/json");
            using var res=await _http.SendAsync(req); var json=await res.Content.ReadAsStringAsync();
            if(!res.IsSuccessStatusCode) return new(){Provider="Gemini",Success=false,Error=json};
            using var doc=JsonDocument.Parse(json);
            var texts=new List<string>();
            if(doc.RootElement.TryGetProperty("candidates",out var cs))
                foreach(var c in cs.EnumerateArray())
                    if(c.TryGetProperty("content",out var content)&&content.TryGetProperty("parts",out var parts))
                        foreach(var p in parts.EnumerateArray()) if(p.TryGetProperty("text",out var t)) texts.Add(t.GetString()??"");
            return new(){Provider="Gemini",Success=true,Result=string.Join("\n",texts)};
        }catch(Exception ex){return new(){Provider="Gemini",Success=false,Error=ex.Message};}
    }
}
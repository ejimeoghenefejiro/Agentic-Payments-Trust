using System.Text;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace AgentTrust.Api;

public sealed class TextPlainInputFormatter:TextInputFormatter
{
    public TextPlainInputFormatter()
    {
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("text/plain"));
        SupportedEncodings.Add(Encoding.UTF8);SupportedEncodings.Add(Encoding.Unicode);
    }
    protected override bool CanReadType(Type type)=>type==typeof(string);
    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context,Encoding encoding)
    {using var reader=new StreamReader(context.HttpContext.Request.Body,encoding);return await InputFormatterResult.SuccessAsync(await reader.ReadToEndAsync(context.HttpContext.RequestAborted));}
}

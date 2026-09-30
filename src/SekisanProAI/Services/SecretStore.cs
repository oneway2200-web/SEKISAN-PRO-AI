using System.Security.Cryptography;
using System.Text;

namespace SekisanProAI.Services;

public sealed class SecretStore
{
    private readonly DatabaseService _db;
    public SecretStore(DatabaseService db)=>_db=db;

    public void Save(string key,string value)
    {
        if(string.IsNullOrWhiteSpace(value)){_db.SetSetting(key,"");return;}
        var plain=Encoding.UTF8.GetBytes(value);
        var encrypted=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
        _db.SetSetting(key,Convert.ToBase64String(encrypted));
    }

    public string Load(string key)
    {
        var raw=_db.GetSetting(key,"");
        if(string.IsNullOrWhiteSpace(raw)) return "";
        try{
            var encrypted=Convert.FromBase64String(raw);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser));
        }catch{return "";}
    }
}
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebPush;
namespace Moz;
public sealed class PushKeys(string folder,IConfiguration cfg){
 private readonly object gate=new();private VapidDetails? current;
 public VapidDetails Get(){lock(gate){if(current!=null)return current;
 var subject=cfg["MOZ_VAPID_SUBJECT"]??"https://moz.runasp.net";
 var pub=cfg["MOZ_VAPID_PUBLIC_KEY"];var priv=cfg["MOZ_VAPID_PRIVATE_KEY"];
 if(!string.IsNullOrWhiteSpace(pub)&&!string.IsNullOrWhiteSpace(priv))return current=new VapidDetails(subject,pub,priv);
 Directory.CreateDirectory(folder);var path=Path.Combine(folder,"webpush-vapid.json");
 if(!File.Exists(path)){var k=VapidHelper.GenerateVapidKeys();try{using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);JsonSerializer.Serialize(stream,new KeyPair(k.PublicKey,k.PrivateKey));}catch(IOException)when(File.Exists(path)){}}
 var pair=JsonSerializer.Deserialize<KeyPair>(File.ReadAllText(path))??throw new InvalidOperationException("Invalid VAPID file");
 return current=new VapidDetails(subject,pair.PublicKey,pair.PrivateKey);
 }}
 public record KeyPair(string PublicKey,string PrivateKey);
}
public static class PushEndpoints{
 static async Task<User?> UserFor(HttpContext c,MozDb db){if(!Guid.TryParse(c.User.FindFirstValue("sub"),out var id))return null;var u=await db.Users.SingleOrDefaultAsync(x=>x.Id==id&&x.Active);if(u!=null&&u.Role is not ("global" or "operator" or "client" or "driver"))return null;if(u?.TenantId is {} tid&&!await db.Tenants.AnyAsync(x=>x.Id==tid&&x.Active))return null;return u;}
 public static bool SafeEndpoint(string? value){if(value==null||value.Length>2000||!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.Scheme!="https"||u.Port!=443||u.UserInfo.Length!=0||!string.IsNullOrEmpty(u.Fragment))return false;var h=u.IdnHost.ToLowerInvariant();return h=="fcm.googleapis.com"||h=="updates.push.services.mozilla.com"||h=="web.push.apple.com"||h.EndsWith(".push.apple.com")||h.EndsWith(".notify.windows.com");}
 static bool SafeKey(string? value,int len){if(string.IsNullOrWhiteSpace(value)||value.Length>200)return false;try{var s=value.Replace('-','+').Replace('_','/');s=s.PadRight((s.Length+3)/4*4,'=');return Convert.FromBase64String(s).Length==len;}catch{return false;}}
 public static void MapPushEndpoints(this RouteGroupBuilder api){
 api.MapGet("/push/key",async(HttpContext c,MozDb db,PushKeys keys)=>{var u=await UserFor(c,db);if(u==null||u.Role=="global")return Results.Forbid();return Results.Ok(new{publicKey=keys.Get().PublicKey});});
 api.MapPost("/push/subscribe",async(DeviceInput x,HttpContext c,MozDb db)=>{
 var u=await UserFor(c,db);if(u==null||u.Role=="global")return Results.Forbid();
 if(!SafeEndpoint(x.Endpoint)||!SafeKey(x.P256dh,65)||!SafeKey(x.Auth,16)||x.Kinds?.Length>2000)return Results.BadRequest(new{error="Inscrição push inválida."});
 var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.Endpoint)));var d=await db.PushDevices.SingleOrDefaultAsync(z=>z.EndpointHash==hash);if(d==null){d=new PushDevice{EndpointHash=hash};db.PushDevices.Add(d);}
 var same=d.UserId==u.Id&&d.P256dh==x.P256dh&&d.Auth==x.Auth&&d.Kinds==x.Kinds;
 d.UserId=u.Id;d.Endpoint=x.Endpoint;d.P256dh=x.P256dh;d.Auth=x.Auth;d.Kinds=x.Kinds;
 if(!same){d.Cursor=DateTimeOffset.UtcNow;d.NextAttempt=d.Cursor;d.Failures=0;}
 await db.SaveChangesAsync();return Results.Ok();});
 api.MapPost("/push/status",async(UnsubscribeInput x,HttpContext c,MozDb db)=>{
 var u=await UserFor(c,db);if(u==null||u.Role=="global")return Results.Forbid();
 if(!SafeEndpoint(x.Endpoint))return Results.BadRequest();
 var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.Endpoint)));
 var d=await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(z=>z.EndpointHash==hash&&z.UserId==u.Id);
 return Results.Ok(new{registered=d!=null,retrying=d!=null&&d.Failures>0});
 });
 api.MapPost("/push/unsubscribe",async(UnsubscribeInput x,HttpContext c,MozDb db)=>{
 var u=await UserFor(c,db);if(u==null)return Results.Unauthorized();if(x.Endpoint==null||x.Endpoint.Length>2000)return Results.BadRequest();var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.Endpoint)));var d=await db.PushDevices.SingleOrDefaultAsync(z=>z.EndpointHash==hash&&z.UserId==u.Id);if(d!=null){db.PushDevices.Remove(d);await db.SaveChangesAsync();}return Results.Ok();});
 }
 public record DeviceInput(string Endpoint,string P256dh,string Auth,string? Kinds);
 public record UnsubscribeInput(string Endpoint);
}
public class PushWorker(IServiceScopeFactory scopes,PushKeys keys,StartupState startup,ILogger<PushWorker> log):BackgroundService{
 protected override async Task ExecuteAsync(CancellationToken ct){
 using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(12)};
 using var push=new WebPushClient(http);
 while(!ct.IsCancellationRequested){
 try{await Task.Delay(TimeSpan.FromSeconds(15),ct);if(!startup.Ready)continue;
 using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<MozDb>();var now=DateTimeOffset.UtcNow;
 var devices=await db.PushDevices.Where(d=>d.NextAttempt<=now).OrderBy(d=>d.NextAttempt).Take(100).ToListAsync(ct);
 foreach(var d in devices){
 if(ct.IsCancellationRequested)break;
 var u=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==d.UserId&&x.Active,ct);
 if(u==null||u.Role is not ("global" or "operator" or "client" or "driver")||!PushEndpoints.SafeEndpoint(d.Endpoint)||(u.TenantId is {} tenant&&!await db.Tenants.AnyAsync(t=>t.Id==tenant&&t.Active,ct))){db.PushDevices.Remove(d);continue;}
 var trips=u.Role switch{"operator"=>db.Trips.Where(t=>t.TenantId==u.TenantId),"client"=>db.Trips.Where(t=>t.TenantId==u.TenantId&&t.ClientId==u.ClientId),"driver" when u.IdentityVerified=>db.Trips.Where(t=>t.DriverId==u.Id),_=>db.Trips.Where(t=>false)};
 var cursor=d.Cursor;var watermark=now.AddSeconds(-5);
 var q=from e in db.Events join t in trips on e.TripId equals t.Id where e.Recorded>cursor&&e.Recorded<=watermark select e;
 var kinds=(d.Kinds??"").Split('|',StringSplitOptions.RemoveEmptyEntries);
 if(kinds.Length>0)q=q.Where(e=>kinds.Contains(e.Kind));
 var count=await q.CountAsync(ct);d.NextAttempt=now.AddSeconds(20);
 if(count==0){if(watermark>d.Cursor)d.Cursor=watermark;continue;}
 try{var latest=await q.OrderByDescending(e=>e.Recorded).ThenByDescending(e=>e.Id).Select(e=>e.Id).FirstAsync(ct);
 var payload=JsonSerializer.Serialize(new{title="MOZ · Operação logística",body=count==1?"Novo evento operacional. Toque para consultar.":$"{count} novos eventos operacionais. Toque para consultar.",url="/?notifications=1",tag="moz-"+latest.ToString("N")});
 await push.SendNotificationAsync(new WebPush.PushSubscription(d.Endpoint,d.P256dh,d.Auth),payload,keys.Get(),ct);d.Cursor=watermark;d.Failures=0;}
 catch(WebPushException ex)when((int)ex.StatusCode is 404 or 410){db.PushDevices.Remove(d);}
 catch(Exception ex){d.Failures=Math.Min(d.Failures+1,10);d.NextAttempt=now.AddSeconds(Math.Min(3600,30*Math.Pow(2,d.Failures)));log.LogWarning("Push delivery failed: {Type}",ex.GetType().Name);}
 }
 await db.SaveChangesAsync(ct);
 }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception ex){log.LogWarning("Push cycle failed: {Type}",ex.GetType().Name);}
 }
 }
}

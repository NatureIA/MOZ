using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Moz;
public static class Api {
 public static object PublicUser(User u)=>new{u.Id,u.Name,u.Role,u.TenantId,u.ClientId,u.IdentityVerified};
 static Guid Actor(HttpContext c)=>Guid.TryParse(c.User.FindFirstValue("sub"),out var id)?id:Guid.Empty;
 static async Task<User?> Current(HttpContext c,MozDb db){
 var u=await db.Users.SingleOrDefaultAsync(x=>x.Id==Actor(c)&&x.Active);
 if(u!=null&&u.Role is not ("global" or "operator" or "client" or "driver"))return null;
 if(u?.TenantId is {} id&&!await db.Tenants.AnyAsync(t=>t.Id==id&&t.Active))return null;return u;
 }
 static IQueryable<Trip> Visible(MozDb db,User u)=>u.Role switch{
 "operator"=>db.Trips.Where(t=>t.TenantId==u.TenantId),
 "client"=>db.Trips.Where(t=>t.TenantId==u.TenantId&&t.ClientId==u.ClientId),
 "driver" when u.IdentityVerified=>db.Trips.Where(t=>t.DriverId==u.Id),
 _=>db.Trips.Where(t=>false)};
 static bool Operator(User? u)=>u?.Role=="operator"&&u.TenantId!=null;
 static IResult Bad(string s)=>Results.BadRequest(new{error=s});
 static bool TextOk(string? s,int max=160)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=max;
 static bool PasswordOk(string? s)=>s is {Length:>=12 and <=128};
 static void Record(MozDb db,User u,string action,string target)=>db.Audits.Add(new Audit{ActorId=u.Id,TenantId=u.TenantId,Action=action,Target=target});
 static async Task<IResult> Save(MozDb db){try{await db.SaveChangesAsync();return Results.Ok();}catch(DbUpdateConcurrencyException){return Results.Conflict(new{error="Registro atualizado por outra operação. Atualize e tente novamente."});}catch(DbUpdateException){return Results.Conflict(new{error="Registro conflitante ou duplicado."});}}
 static async Task<Stage?> CurrentStage(MozDb db,Trip t)=>await db.Stages.SingleOrDefaultAsync(s=>s.TripId==t.Id&&s.Order==t.CurrentStage);
 public static void MapMozEndpoints(this RouteGroupBuilder api,string storage,IConfiguration cfg){
 api.MapGet("/me",async (HttpContext c,MozDb db)=>{var u=await Current(c,db);return u==null?Results.Unauthorized():Results.Ok(PublicUser(u));});
 api.MapGet("/tenants",async(HttpContext c,MozDb db)=>{var u=await Current(c,db);if(u?.Role!="global")return Results.Forbid();return Results.Ok(await db.Tenants.AsNoTracking().ToListAsync());});
 api.MapPost("/tenants",async(TenantInput x,HttpContext c,MozDb db,IPasswordHasher<User> hasher)=>{
 var u=await Current(c,db);if(u?.Role!="global")return Results.Forbid();
 if(!TextOk(x.Name)||!TextOk(x.AdminName)||!TextOk(x.Email,256)||!x.Email.Contains('@')||!PasswordOk(x.Password)||x.TripLimit<0)return Bad("Confira os dados do operador e senha com pelo menos 12 caracteres.");
 var tenant=new Tenant{Name=x.Name.Trim(),Plan=x.Plan??"",TripLimit=x.TripLimit};var admin=new User{Role="operator",TenantId=tenant.Id,Name=x.AdminName.Trim(),Login=x.Email.Trim().ToLowerInvariant(),IdentityVerified=true};admin.PasswordHash=hasher.HashPassword(admin,x.Password);db.Tenants.Add(tenant);db.Users.Add(admin);Record(db,u,"Criar operador",tenant.Id.ToString());return await Save(db);
 });
 api.MapPost("/tenants/{id:guid}/active",async(Guid id,ActiveInput x,HttpContext c,MozDb db)=>{var u=await Current(c,db);if(u?.Role!="global")return Results.Forbid();var t=await db.Tenants.FindAsync(id);if(t==null)return Results.NotFound();t.Active=x.Active;Record(db,u,"Alterar acesso do operador",id.ToString());return await Save(db);});
 api.MapGet("/drivers/pending",async(HttpContext c,MozDb db)=>{var u=await Current(c,db);if(u?.Role!="global")return Results.Forbid();return Results.Ok(await db.Users.Where(x=>x.Role=="driver"&&!x.IdentityVerified).Select(x=>new{x.Id,x.Name,x.Cpf,x.Phone}).ToListAsync());});
 api.MapPost("/drivers/{id:guid}/verify",async(Guid id,VerifyInput x,HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(u?.Role!="global")return Results.Forbid();
 if(!TextOk(x.Evidence,500))return Bad("Informe a referência da conferência documental externa de identidade.");
 var d=await db.Users.SingleOrDefaultAsync(x=>x.Id==id&&x.Role=="driver"&&x.Active);if(d==null)return Results.NotFound();
 d.IdentityVerified=true;var trips=await db.Trips.Where(t=>t.DriverCpf==d.Cpf&&t.DriverId==null&&t.Status=="Programada").ToListAsync();foreach(var t in trips){t.DriverId=d.Id;t.DriverName=d.Name;db.Events.Add(Engine.Event(t,"Motorista vinculado","Identidade conferida; demanda liberada","Sistema"));}
 Record(db,u,"Verificação de identidade: "+x.Evidence,id.ToString());return await Save(db);
 });
 api.MapGet("/catalog",async(HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(!Operator(u))return Results.Forbid();var id=u!.TenantId;
 return Results.Ok(new{clients=await db.Clients.Where(x=>x.TenantId==id).ToListAsync(),registry=await db.Registry.Where(x=>x.TenantId==id).ToListAsync(),places=await db.Places.Where(x=>x.TenantId==id).ToListAsync(),users=await db.Users.Where(x=>x.TenantId==id).Select(x=>new{x.Id,x.Name,x.Login,x.Role,x.ClientId,x.Active}).ToListAsync()});
 });
 api.MapPost("/clients",async(NameInput x,HttpContext c,MozDb db)=>{var u=await Current(c,db);if(!Operator(u))return Results.Forbid();if(!TextOk(x.Name))return Bad("Nome obrigatório.");var cl=new Client{TenantId=u!.TenantId!.Value,Name=x.Name.Trim()};db.Clients.Add(cl);Record(db,u,"Criar cliente",cl.Id.ToString());return await Save(db);});
 api.MapPost("/users",async(UserInput x,HttpContext c,MozDb db,IPasswordHasher<User> hasher)=>{
 var u=await Current(c,db);if(!Operator(u))return Results.Forbid();if(!TextOk(x.Name)||!TextOk(x.Email,256)||!x.Email.Contains('@')||!PasswordOk(x.Password)||x.Role is not ("operator" or "client"))return Bad("Dados inválidos.");
 if(x.Role=="client"&&!await db.Clients.AnyAsync(z=>z.Id==x.ClientId&&z.TenantId==u!.TenantId))return Bad("Cliente inválido.");
 var a=new User{TenantId=u!.TenantId,ClientId=x.Role=="client"?x.ClientId:null,Name=x.Name.Trim(),Login=x.Email.Trim().ToLowerInvariant(),Role=x.Role,IdentityVerified=true};a.PasswordHash=hasher.HashPassword(a,x.Password);db.Users.Add(a);Record(db,u,"Criar usuário",a.Id.ToString());return await Save(db);
 });
 api.MapPost("/users/{id:guid}/active",async(Guid id,ActiveInput x,HttpContext c,MozDb db)=>{var u=await Current(c,db);if(!Operator(u)||u!.Id==id)return Results.Forbid();var a=await db.Users.SingleOrDefaultAsync(z=>z.Id==id&&z.TenantId==u.TenantId);if(a==null)return Results.NotFound();a.Active=x.Active;Record(db,u,"Alterar acesso",id.ToString());return await Save(db);});
 api.MapPost("/registry",async(RegistryInput x,HttpContext c,MozDb db)=>{var u=await Current(c,db);if(!Operator(u))return Results.Forbid();if(!TextOk(x.Name)||!TextOk(x.Reference)||x.Kind is not("Transportadora" or "Cavalo" or "Carreta" or "Veículo" or "Contêiner"))return Bad("Cadastro inválido.");var a=new Registry{TenantId=u!.TenantId!.Value,Kind=x.Kind,Name=x.Name,Reference=x.Reference};db.Registry.Add(a);Record(db,u,"Cadastrar "+x.Kind,a.Id.ToString());return await Save(db);});
 api.MapPost("/places",async(PlaceInput x,HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(!Operator(u))return Results.Forbid();if(!TextOk(x.Name)||!TextOk(x.Address,500)||!Engine.ValidPoint(x.Lat,x.Lon)||x.Radius is <10 or >10000||!Engine.ValidPolygon(x.Polygon))return Bad("Cerca inválida.");
 if(x.ParentId!=null&&!await db.Places.AnyAsync(z=>z.Id==x.ParentId&&z.TenantId==u!.TenantId))return Bad("Estabelecimento principal inválido.");
 var p=new Place{TenantId=u!.TenantId!.Value,Name=x.Name,Address=x.Address,Lat=x.Lat,Lon=x.Lon,Radius=x.Radius,Polygon=x.Polygon,ParentId=x.ParentId};db.Places.Add(p);Record(db,u,"Criar cerca",p.Id.ToString());return await Save(db);
 });
 api.MapGet("/trips",async(HttpContext c,MozDb db,int? page)=>{
 var u=await Current(c,db);if(u==null)return Results.Unauthorized();var q=Visible(db,u);var n=Math.Clamp(page??0,0,10000);
 return Results.Ok(new{total=await q.CountAsync(),items=await q.OrderByDescending(x=>x.Created).Skip(n*100).Take(100).ToListAsync()});
 });
 api.MapPost("/trips",async(TripInput x,HttpContext c,MozDb db,IHubContext<MozHub> hub)=>{
 var u=await Current(c,db);if(!Operator(u))return Results.Forbid();
 if(!await db.Clients.AnyAsync(z=>z.Id==x.ClientId&&z.TenantId==u!.TenantId))return Bad("Cliente inválido.");
 var cpf=Engine.Digits(x.DriverCpf);if(!Engine.ValidCpf(cpf)||x.Stages==null||x.Stages.Length is <1 or >50||x.SpeedLimit is <10 or >150||x.MaxAccuracy is <5 or >200||x.StopSeconds is <30 or >3600||x.GapSeconds is <30 or >3600)return Bad("CPF, etapas ou parâmetros inválidos.");
 var tenant=await db.Tenants.FindAsync(u!.TenantId!.Value);if(tenant==null)return Results.Forbid();
 if(tenant.TripLimit>0&&await db.Trips.CountAsync(z=>z.TenantId==tenant.Id&&z.Finished==null)>=tenant.TripLimit)return Bad("Limite de viagens ativas atingido.");
 var driver=await db.Users.SingleOrDefaultAsync(z=>z.Cpf==cpf&&z.Role=="driver"&&z.IdentityVerified&&z.Active);
 var t=new Trip{TenantId=tenant.Id,ClientId=x.ClientId,DriverCpf=cpf,DriverId=driver?.Id,DriverName=driver?.Name??"",Carrier=x.Carrier??"",Tractor=x.Tractor??"",Trailer=x.Trailer??"",Container=x.Container??"",Scheduled=x.Scheduled,SpeedLimit=x.SpeedLimit,MaxAccuracy=x.MaxAccuracy,StopSeconds=x.StopSeconds,GapSeconds=x.GapSeconds,Code="MOZ-"+Guid.NewGuid().ToString("N").ToUpperInvariant()};
 if(!string.IsNullOrWhiteSpace(x.PlannedRoute)&&(!Engine.ValidRoute(x.PlannedRoute)||x.RouteTolerance is <30 or >10000))return Bad("Rota planejada ou tolerância inválida.");
 var stages=new List<Stage>();
 for(int i=0;i<x.Stages.Length;i++){var s=x.Stages[i];var p=await db.Places.SingleOrDefaultAsync(z=>z.Id==s.PlaceId&&z.TenantId==tenant.Id);if(p==null||!TextOk(s.Label)||s.SlaMinutes<0||s.FinishOnPod&&!s.RequirePod)return Bad("Etapa inválida.");
 stages.Add(new Stage{TripId=t.Id,Order=i,Label=s.Label,Kind=s.Kind??"",Address=p.Address,Lat=p.Lat,Lon=p.Lon,Radius=p.Radius,Polygon=p.Polygon,RequirePod=s.RequirePod,FinishOnPod=s.FinishOnPod,SlaMinutes=s.SlaMinutes});}
 db.Trips.Add(t);db.Stages.AddRange(stages);if(!string.IsNullOrWhiteSpace(x.PlannedRoute))db.Plans.Add(new TripPlan{TripId=t.Id,Coordinates=x.PlannedRoute,ToleranceMetres=x.RouteTolerance});db.Events.Add(Engine.Event(t,"Programada",driver==null?"Viagem programada, motorista pendente de verificação":"Nova viagem programada","Operador",actor:u.Id));Record(db,u,"Programar viagem",t.Id.ToString());var result=await Save(db);if(result is Microsoft.AspNetCore.Http.IStatusCodeHttpResult sr&&sr.StatusCode==200){try{await MozRealtime.Publish(hub,t);}catch{}}return result;
 });
 api.MapGet("/trips/{id:guid}",async(Guid id,HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(u==null)return Results.Unauthorized();var t=await Visible(db,u).SingleOrDefaultAsync(x=>x.Id==id);if(t==null)return Results.NotFound();
 return Results.Ok(new{trip=t,plan=await db.Plans.SingleOrDefaultAsync(x=>x.TripId==id),stages=await db.Stages.Where(x=>x.TripId==id).OrderBy(x=>x.Order).ToListAsync(),events=await db.Events.Where(x=>x.TripId==id).OrderBy(x=>x.At).Take(10000).ToListAsync(),pods=await db.Pods.Where(x=>x.TripId==id).ToListAsync()});
 });
 api.MapGet("/trips/{id:guid}/fixes",async(Guid id,HttpContext c,MozDb db,int? page)=>{
 var u=await Current(c,db);if(u==null)return Results.Unauthorized();if(!await Visible(db,u).AnyAsync(x=>x.Id==id))return Results.NotFound();
 return Results.Ok(await db.Fixes.Where(x=>x.TripId==id&&x.Accepted).OrderBy(x=>x.At).Skip(Math.Clamp(page??0,0,100000)*1000).Take(1000).Select(x=>new{x.At,x.Lat,x.Lon,x.Accuracy,x.Speed}).ToListAsync());
 });
 api.MapPost("/trips/{id:guid}/action",async(Guid id,ActionInput x,HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(u==null)return Results.Unauthorized();var t=await Visible(db,u).SingleOrDefaultAsync(z=>z.Id==id);if(t==null)return Results.NotFound();
 if(Engine.Terminal(t))return Bad("Viagem encerrada.");
 var s=await CurrentStage(db,t);
 if(x.Action=="start"&&u.Role=="driver"&&t.Started==null){
 if(await db.Trips.AnyAsync(z=>z.Id!=id&&z.DriverId==u.Id&&z.Started!=null&&z.Finished==null&&z.Status!="Cancelada"))return Bad("Já existe uma viagem ativa.");
 t.Started=DateTimeOffset.UtcNow;t.Status="Em operação";db.Events.Add(Engine.Event(t,"Operação iniciada","Motorista iniciou operação","Motorista",actor:u.Id));
 }else if(x.Action=="work-start"&&u.Role=="driver"&&s?.Arrived!=null&&s.Departed==null&&s.WorkStart==null){s.WorkStart=DateTimeOffset.UtcNow;t.Status="Em "+s.Kind;db.Events.Add(Engine.Event(t,"Início de "+s.Kind,"Início confirmado pelo motorista","Motorista",actor:u.Id,stage:s));}
 else if(x.Action=="work-end"&&u.Role=="driver"&&s?.WorkStart!=null&&s.WorkEnd==null&&s.Departed==null){s.WorkEnd=DateTimeOffset.UtcNow;t.Status=s.RequirePod?"Aguardando comprovante":s.Kind+" concluída";db.Events.Add(Engine.Event(t,"Fim de "+s.Kind,"Término confirmado pelo motorista","Motorista",actor:u.Id,stage:s));}
 else if(x.Action=="occurrence"&&(u.Role is "driver" or "operator")&&TextOk(x.Message,1000)){db.Events.Add(Engine.Event(t,"Ocorrência",x.Message!,u.Role,actor:u.Id,stage:s));}
 else if(x.Action=="cancel"&&u.Role=="operator"&&TextOk(x.Message,1000)){t.Status="Cancelada";t.Finished=DateTimeOffset.UtcNow;db.Events.Add(Engine.Event(t,"Cancelada",x.Message!,"Operador",actor:u.Id));}
 else return Bad("Ação indisponível para este estado.");
 Record(db,u,x.Action,t.Id.ToString());return await Save(db);
 });
 api.MapPost("/trips/{id:guid}/fixes",async(Guid id,FixInput[] input,HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(u?.Role!="driver")return Results.Forbid();var t=await Visible(db,u).SingleOrDefaultAsync(z=>z.Id==id);
 if(t==null)return Results.NotFound();if(t.Started==null)return Bad("Viagem não está ativa.");
 if(Engine.Terminal(t)){var ids=input.Select(z=>z.Id).ToArray();var saved=await db.Fixes.Where(z=>z.TripId==id&&ids.Contains(z.Id)).Select(z=>z.Id).ToListAsync();return Results.Ok(new{acceptedIds=saved,status=t.Status});}
 if(input.Length is <1 or >100)return Bad("Lote inválido.");
 var count=await db.Stages.CountAsync(s=>s.TripId==id);var stage=await CurrentStage(db,t);var plan=await db.Plans.SingleOrDefaultAsync(p=>p.TripId==id);
 var processed=new List<Guid>();
 foreach(var x in input.OrderBy(z=>z.At)){
 if(x.Id==Guid.Empty||!Engine.ValidPoint(x.Lat,x.Lon)||!double.IsFinite(x.Accuracy)||x.Accuracy<0||x.Speed is <0 or >250||x.At<t.Started.Value.AddMinutes(-1)||x.At>DateTimeOffset.UtcNow.AddMinutes(2))continue;
 if(processed.Contains(x.Id)||await db.Fixes.AnyAsync(z=>z.Id==x.Id&&z.TripId==id)){processed.Add(x.Id);continue;}
 var valid=!Engine.Terminal(t)&&x.Accuracy<=t.MaxAccuracy&&(t.LastFix==null||x.At>t.LastFix);
 var f=new Fix{Id=x.Id,TripId=id,At=x.At,Lat=x.Lat,Lon=x.Lon,Accuracy=x.Accuracy,Speed=x.Speed,Accepted=valid};db.Fixes.Add(f);
 var old=t.CurrentStage;Engine.Process(t,stage,count,f,db);Engine.AuditRoute(t,plan,f,db);if(t.CurrentStage!=old)stage=await CurrentStage(db,t);processed.Add(x.Id);
 }
 try{await db.SaveChangesAsync();return Results.Ok(new{acceptedIds=processed,status=t.Status});}catch(DbUpdateException){return Results.Conflict(new{error="Lote concorrente; tente novamente."});}
 });
 api.MapPost("/trips/{id:guid}/pod",async(Guid id,HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(u?.Role!="driver")return Results.Forbid();var t=await Visible(db,u).SingleOrDefaultAsync(z=>z.Id==id);if(t==null)return Results.NotFound();
 if(!c.Request.HasFormContentType)return Bad("Envie uma fotografia ou PDF.");
 var form=await c.Request.ReadFormAsync();if(!Guid.TryParse(form["id"].ToString(),out var podId))return Bad("Identificador inválido.");
 var existing=await db.Pods.SingleOrDefaultAsync(z=>z.Id==podId&&z.TripId==id);if(existing!=null)return Results.Ok(new{status=t.Status,id=existing.Id});
 if(Engine.Terminal(t)||t.Started==null)return Bad("Viagem não está ativa.");var s=await CurrentStage(db,t);if(s?.Arrived==null)return Bad("Chegada à etapa ainda não registrada.");
 var file=form.Files.GetFile("file");long max=Math.Clamp(cfg.GetValue("Storage:MaxFileSizeMB",20),1,20)*1024L*1024L;
 if(file==null||file.Length<4||file.Length>max)return Bad("Arquivo inválido ou muito grande.");
 using var ms=new MemoryStream();await file.CopyToAsync(ms);var data=ms.ToArray();
 string? mime=data[0]==255&&data[1]==216&&data[2]==255?"image/jpeg":data.Length>=8&&data.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})?"image/png":data.Length>=12&&Encoding.ASCII.GetString(data,0,4)=="RIFF"&&Encoding.ASCII.GetString(data,8,4)=="WEBP"?"image/webp":data.Length>=5&&Encoding.ASCII.GetString(data,0,5)=="%PDF-"?"application/pdf":null;
 var ext=mime switch{"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp","application/pdf"=>".pdf",_=>""};
 if(mime==null||!(cfg["Storage:AllowedExtensions"]??".jpg,.jpeg,.png,.webp,.pdf").Split(',').Contains(ext,StringComparer.OrdinalIgnoreCase))return Bad("Formato de arquivo não permitido.");
 DateTimeOffset captured=DateTimeOffset.TryParse(form["captured"].ToString(),out var when)?when:DateTimeOffset.UtcNow;
 if(captured<t.Started.Value.AddMinutes(-1)||captured>DateTimeOffset.UtcNow.AddMinutes(2))return Bad("Horário de captura inválido.");
 var pos=await db.Fixes.Where(z=>z.TripId==id&&z.Accepted&&z.At<=captured&&z.At>=captured.AddMinutes(-2)).OrderByDescending(z=>z.At).FirstOrDefaultAsync();
 bool verified=pos!=null&&Engine.Contains(pos.Lat,pos.Lon,s.Lat,s.Lon,s.Radius,s.Polygon);
 var doc=new Pod{Id=podId,TripId=id,StageId=s.Id,DriverId=u.Id,ContentType=mime,FileName="comprovante"+ext,Bytes=data.LongLength,Sha256=Convert.ToHexString(SHA256.HashData(data)),Captured=captured,Lat=pos?.Lat,Lon=pos?.Lon,Accuracy=pos?.Accuracy,LocationVerified=verified};
 var path=Path.Combine(storage,podId.ToString("N"));try{await using(var fs=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){await fs.WriteAsync(data);await fs.FlushAsync();}}catch(IOException){return Results.Conflict(new{error="Documento já está sendo processado."});}
 db.Pods.Add(doc);db.Events.Add(Engine.Event(t,"Comprovante recebido",verified?"Comprovante armazenado com localização compatível":"Comprovante armazenado; localização não verificada","Motorista",actor:u.Id,stage:s));
 if(s.FinishOnPod&&verified){var count=await db.Stages.CountAsync(z=>z.TripId==id);Engine.Advance(t,s,count,db,DateTimeOffset.UtcNow);}
 else if(s.Departed!=null){var count=await db.Stages.CountAsync(z=>z.TripId==id);Engine.Advance(t,s,count,db,DateTimeOffset.UtcNow);}
 try{await db.SaveChangesAsync();return Results.Ok(new{status=t.Status,id=doc.Id});}catch{File.Delete(path);throw;}
 });
 api.MapGet("/pods/{id:guid}",async(Guid id,HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(u==null)return Results.Unauthorized();var p=await db.Pods.SingleOrDefaultAsync(x=>x.Id==id);if(p==null||!await Visible(db,u).AnyAsync(x=>x.Id==p.TripId))return Results.NotFound();
 var path=Path.Combine(storage,p.Id.ToString("N"));return File.Exists(path)?Results.File(path,"application/octet-stream",p.FileName):Results.NotFound();
 });
 api.MapGet("/notices",async(HttpContext c,MozDb db)=>{
 var u=await Current(c,db);if(u==null)return Results.Unauthorized();var q=from e in db.Events join t in Visible(db,u) on e.TripId equals t.Id select new{e,t.Code};
 var unread=await q.CountAsync(x=>!db.Reads.Any(r=>r.UserId==u.Id&&r.EventId==x.e.Id));
 var recent=await q.OrderByDescending(x=>x.e.Recorded).Take(100).Select(x=>new{x.e.Id,x.e.TripId,x.e.Kind,x.e.Message,x.e.At,x.Code,Read=db.Reads.Any(r=>r.UserId==u.Id&&r.EventId==x.e.Id)}).ToListAsync();
 return Results.Ok(new{unread,items=recent});
 });
 api.MapPost("/notices/{id:guid}/read",async(Guid id,HttpContext c,MozDb db)=>{var u=await Current(c,db);if(u==null)return Results.Unauthorized();if(!await db.Events.AnyAsync(x=>x.Id==id&&Visible(db,u).Any(t=>t.Id==x.TripId)))return Results.NotFound();if(!await db.Reads.AnyAsync(r=>r.UserId==u.Id&&r.EventId==id))db.Reads.Add(new ReadNotice{UserId=u.Id,EventId=id});return await Save(db);});
 api.MapGet("/reports/trips.csv",async(HttpContext c,MozDb db,DateTimeOffset? from,DateTimeOffset? to)=>{
 var u=await Current(c,db);if(u==null||u.Role is not("operator" or "client"))return Results.Forbid();
 if(from.HasValue&&to.HasValue&&from.Value>=to.Value)return Bad("Período inválido.");
 var q=Visible(db,u).AsNoTracking();
 if(from.HasValue)q=q.Where(t=>t.Scheduled>=from.Value);if(to.HasValue)q=q.Where(t=>t.Scheduled<to.Value);
 var rows=await q.OrderByDescending(t=>t.Scheduled).ThenBy(t=>t.Id).Take(10001).ToListAsync();
 if(rows.Count>10000)return Bad("Selecione um período menor para exportar até 10.000 viagens.");
 var csv=new StringBuilder("Código;Status;Motorista;Transportadora;Placa;Contêiner;Programação UTC;Início UTC;Fim UTC;Distância km;Movimento h;Parado h;Velocidade máxima km/h\r\n");
 string Cell(string? value){var v=value??"";var trimmed=v.TrimStart();if(trimmed.Length>0&&"=+-@".Contains(trimmed[0]))v="'"+v;return "\""+v.Replace("\"","\"\"")+"\"";}
 string Num(double value)=>value.ToString("0.00",System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
 string Date(DateTimeOffset? value)=>value?.ToUniversalTime().ToString("O")??"";
 foreach(var t in rows)csv.AppendLine(string.Join(";",new[]{Cell(t.Code),Cell(t.Status),Cell(t.DriverName),Cell(t.Carrier),Cell(t.Tractor),Cell(t.Container),Cell(Date(t.Scheduled)),Cell(Date(t.Started)),Cell(Date(t.Finished)),Num(t.DistanceKm),Num(t.MovingSeconds/3600),Num(t.IdleSeconds/3600),Num(t.MaxSpeed)}));
 c.Response.Headers.CacheControl="no-store";
 return Results.File(Encoding.UTF8.GetBytes("\uFEFF"+csv.ToString()),"text/csv; charset=utf-8","moz-viagens.csv");
 });
 api.MapGet("/reports",async(HttpContext c,MozDb db)=>{var u=await Current(c,db);if(u==null||u.Role is not("operator" or "client"))return Results.Forbid();var q=Visible(db,u);return Results.Ok(new{total=await q.CountAsync(),finalized=await q.CountAsync(t=>t.Status=="Finalizada"),active=await q.CountAsync(t=>t.Finished==null&&t.Started!=null),scheduled=await q.CountAsync(t=>t.Status=="Programada"),distanceKm=await q.SumAsync(t=>t.DistanceKm),movingHours=await q.SumAsync(t=>t.MovingSeconds)/3600,idleHours=await q.SumAsync(t=>t.IdleSeconds)/3600});});
 }
}
public record TenantInput(string Name,string AdminName,string Email,string Password,string? Plan,int TripLimit);
public record ActiveInput(bool Active);
public record VerifyInput(string Evidence);
public record NameInput(string Name);
public record UserInput(string Name,string Email,string Password,string Role,Guid? ClientId);
public record RegistryInput(string Kind,string Name,string Reference);
public record PlaceInput(string Name,string Address,double Lat,double Lon,double Radius,string? Polygon,Guid? ParentId);
public record StageInput(Guid PlaceId,string Label,string? Kind,bool RequirePod,bool FinishOnPod,int SlaMinutes);
public record TripInput(Guid ClientId,string DriverCpf,string? Carrier,string? Tractor,string? Trailer,string? Container,DateTimeOffset Scheduled,double SpeedLimit,double MaxAccuracy,int StopSeconds,int GapSeconds,StageInput[] Stages,string? PlannedRoute=null,double RouteTolerance=200);
public record ActionInput(string Action,string? Message);
public record FixInput(Guid Id,DateTimeOffset At,double Lat,double Lon,double Accuracy,double? Speed);

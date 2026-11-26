using Moz;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;

var b=WebApplication.CreateBuilder(args);
var cfg=b.Configuration;
var cs=cfg.GetConnectionString("DefaultConnection");
var key=cfg["Jwt:Key"];
var startup=new StartupState();
var signing=new SymmetricSecurityKey(!string.IsNullOrWhiteSpace(key)&&Encoding.UTF8.GetByteCount(key)>=32?Encoding.UTF8.GetBytes(key):System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
b.Services.AddSingleton(startup);
var issuer=cfg["Jwt:Issuer"]??"MOZ";var audience=cfg["Jwt:Audience"]??"MOZ.Users";
b.Services.AddDbContext<MozDb>(o=>o.UseSqlServer(cs??""));
b.Services.AddSingleton<IPasswordHasher<User>,PasswordHasher<User>>();
b.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>{
 o.MapInboundClaims=false;
 o.TokenValidationParameters=new(){ValidateIssuer=true,ValidIssuer=issuer,ValidateAudience=true,ValidAudience=audience,ValidateIssuerSigningKey=true,IssuerSigningKey=signing,ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(30),NameClaimType="sub",RoleClaimType="role"};
 o.Events=new JwtBearerEvents{OnMessageReceived=c=>{c.Token=c.Request.Cookies["moz_session"];return Task.CompletedTask;}};
});
b.Services.AddAuthorization();b.Services.AddSignalR();
var keysPath=Path.GetFullPath(cfg["DataProtection:KeysPath"]??"../private/keys",b.Environment.ContentRootPath);
b.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("MOZ");
b.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("auth",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=12,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
b.Services.AddHostedService<MonitorWorker>();b.Services.AddSingleton(new PushKeys(keysPath,cfg));b.Services.AddHostedService<PushWorker>();
b.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o=>o.MultipartBodyLengthLimit=24*1024*1024);
var app=b.Build();
app.UseExceptionHandler(e=>e.Run(async c=>{c.Response.StatusCode=500;await c.Response.WriteAsJsonAsync(new{error="Falha interna. Consulte o administrador."});}));
app.UseHsts();app.UseHttpsRedirection();
app.Use(async(c,next)=>{
 c.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https://tiles.openfreemap.org; connect-src 'self' https://tiles.openfreemap.org; font-src 'self' data: https://tiles.openfreemap.org; worker-src 'self' blob:; frame-src 'none'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
 c.Response.Headers["X-Content-Type-Options"]="nosniff";
 if(!c.Request.IsHttps&&(c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/hubs"))){c.Response.StatusCode=403;await c.Response.WriteAsJsonAsync(new{error="HTTPS obrigatório para dados e autenticação."});return;}c.Response.Headers["Referrer-Policy"]="no-referrer";c.Response.Headers["X-Frame-Options"]="DENY";
 if((c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/hubs"))&&!startup.Ready){c.Response.StatusCode=503;await c.Response.WriteAsJsonAsync(new{error="MOZ inicializando ou indisponível."});return;}
 if(c.Request.Path.StartsWithSegments("/api")){c.Response.Headers.CacheControl="no-store";if((c.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE") && c.Request.Headers["X-MOZ-Request"]!="1"){c.Response.StatusCode=403;return;}}
 await next();
});
app.UseDefaultFiles();app.UseStaticFiles();app.UseRouting();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
app.Use(async(c,next)=>{
 await next();
 if(c.Response.StatusCode>=200&&c.Response.StatusCode<300&&c.Request.Method=="POST"&&c.Request.Path.StartsWithSegments("/api/trips")&&c.User.Identity?.IsAuthenticated==true){
 try{
 var db=c.RequestServices.GetRequiredService<MozDb>();var hub=c.RequestServices.GetRequiredService<IHubContext<MozHub>>();
 var segments=c.Request.Path.Value!.Split('/',StringSplitOptions.RemoveEmptyEntries);
 if(segments.Length>=3&&Guid.TryParse(segments[2],out var tid)){
 var t=await db.Trips.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==tid);
 if(t!=null)await MozRealtime.Publish(hub,t);
 }else if(segments.Length==2&&Guid.TryParse(c.User.FindFirstValue("sub"),out var uid)){
 var u=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==uid&&x.Active&&(x.Role=="global"||x.Role=="operator"||x.Role=="client"||x.Role=="driver"));
 if(u?.TenantId is {} tenant)await hub.Clients.Group("operator:"+tenant).SendAsync("changed");
 }
 }catch(Exception ex){app.Logger.LogWarning("Live update dispatch failed: {Type}",ex.GetType().Name);}
 }
});
var storage=Path.GetFullPath(cfg["Storage:RootPath"]??"../private/documents",app.Environment.ContentRootPath);
var webroot=Path.GetFullPath(app.Environment.WebRootPath??Path.Combine(app.Environment.ContentRootPath,"wwwroot"));
app.MapGet("/health",()=>Results.Ok(new{app="MOZ",status=startup.Ready?"ok":startup.Failed?"error":"initializing",database=startup.Ready?"ready":startup.Failed?"error":"waiting",version="0.2.8",databaseStep=startup.Step,databaseErrorType=startup.ErrorType,databaseErrorNumber=startup.ErrorNumber}));
app.Lifetime.ApplicationStarted.Register(()=>_ = Task.Run(async()=>{
 for(var attempt=1;attempt<=4;attempt++){
 try{
 startup.Step="connection-config";if(string.IsNullOrWhiteSpace(cs))throw new InvalidOperationException("Database configuration missing");
 startup.Step="jwt-config";if(string.IsNullOrWhiteSpace(key)||Encoding.UTF8.GetByteCount(key)<32)throw new InvalidOperationException("JWT configuration missing or too short");
 startup.Step="private-keys";Directory.CreateDirectory(keysPath);
 startup.Step="private-storage";
 if(!string.Equals(cfg["Storage:Provider"]??"Local","Local",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Storage provider not implemented");
 if(storage.StartsWith(webroot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||storage.Equals(webroot,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Storage must be private");
 Directory.CreateDirectory(storage);
 using var scope=app.Services.CreateScope();
 var db=scope.ServiceProvider.GetRequiredService<MozDb>();
 startup.Step="ensure-created";await db.Database.EnsureCreatedAsync();
 startup.Step="route-schema";await db.Database.ExecuteSqlRawAsync("""
 IF OBJECT_ID(N'dbo.Plans',N'U') IS NULL
 CREATE TABLE [dbo].[Plans](
 [TripId] uniqueidentifier NOT NULL PRIMARY KEY,
 [Coordinates] nvarchar(max) NOT NULL,
 [ToleranceMetres] float NOT NULL,
 [Outside] bit NOT NULL,
 [OutsideCount] int NOT NULL,
 [InsideCount] int NOT NULL,
 CONSTRAINT [FK_Plans_Trips_TripId] FOREIGN KEY([TripId]) REFERENCES [dbo].[Trips]([Id])
 );
 """);
 startup.Step="push-schema";await db.Database.ExecuteSqlRawAsync("""
 IF OBJECT_ID(N'dbo.PushDevices',N'U') IS NULL BEGIN
 CREATE TABLE [dbo].[PushDevices]([Id] uniqueidentifier NOT NULL PRIMARY KEY,[UserId] uniqueidentifier NOT NULL,[EndpointHash] nvarchar(64) NOT NULL,[Endpoint] nvarchar(max) NOT NULL,[P256dh] nvarchar(max) NOT NULL,[Auth] nvarchar(max) NOT NULL,[Cursor] datetimeoffset NOT NULL,[NextAttempt] datetimeoffset NOT NULL,[Failures] int NOT NULL,[Kinds] nvarchar(max) NULL,CONSTRAINT [FK_PushDevices_Users_UserId] FOREIGN KEY([UserId]) REFERENCES [dbo].[Users]([Id]));
 CREATE UNIQUE INDEX [IX_PushDevices_EndpointHash] ON [dbo].[PushDevices]([EndpointHash]);
 CREATE INDEX [IX_PushDevices_NextAttempt] ON [dbo].[PushDevices]([NextAttempt]);
 END;
 """);
 startup.Step="client-driver-schema";await db.Database.ExecuteSqlRawAsync("""
 IF COL_LENGTH(N'dbo.Clients',N'Cnpj') IS NULL
 ALTER TABLE [dbo].[Clients] ADD [Cnpj] nvarchar(14) NULL;
 IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Clients') AND name=N'IX_Clients_TenantId_Cnpj')
 EXEC(N'CREATE UNIQUE INDEX [IX_Clients_TenantId_Cnpj] ON [dbo].[Clients]([TenantId],[Cnpj]) WHERE [Cnpj] IS NOT NULL');
 IF OBJECT_ID(N'dbo.OperatorDrivers',N'U') IS NULL
 CREATE TABLE [dbo].[OperatorDrivers]([TenantId] uniqueidentifier NOT NULL,[Cpf] nvarchar(11) NOT NULL,[Name] nvarchar(160) NOT NULL,CONSTRAINT [PK_OperatorDrivers] PRIMARY KEY([TenantId],[Cpf]));
 """);
 startup.Step="tracking-schema";await db.Database.ExecuteSqlRawAsync("""
 IF COL_LENGTH(N'dbo.Trips',N'TrackingSince') IS NULL
 ALTER TABLE [dbo].[Trips] ADD [TrackingSince] datetimeoffset NULL;
 IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Trips') AND name=N'IX_Trips_DriverCpf')
 EXEC(N'CREATE UNIQUE INDEX [IX_Trips_DriverCpf] ON [dbo].[Trips]([DriverCpf]) WHERE [TrackingSince] IS NOT NULL AND [Finished] IS NULL');
 """);
 startup.Step="stop-audit-schema";await db.Database.ExecuteSqlRawAsync("""
 IF COL_LENGTH(N'dbo.Fixes',N'TrackingSince') IS NULL
 ALTER TABLE [dbo].[Fixes] ADD [TrackingSince] datetimeoffset NULL;
 """);
 startup.Step="cargo-endpoints-schema";await db.Database.ExecuteSqlRawAsync("""
 IF COL_LENGTH(N'dbo.Stages',N'CargoRole') IS NULL
 ALTER TABLE [dbo].[Stages] ADD [CargoRole] nvarchar(max) NULL;
 """);
 startup.Step="check-global";if(!await db.Users.AnyAsync(x=>x.Role=="global")){
 startup.Step="seed-credentials";var email=cfg["SeedAdmin:Email"]?.Trim().ToLowerInvariant();var pass=cfg["SeedAdmin:Password"];
 if(string.IsNullOrWhiteSpace(email)||string.IsNullOrWhiteSpace(pass)||pass.Length<12)throw new InvalidOperationException("Global bootstrap credentials missing or password too short");
 startup.Step="seed-global";var u=new User{Role="global",Name=cfg["SeedAdmin:Name"]??"Administrador MOZ",Login=email,IdentityVerified=true};u.PasswordHash=scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(u,pass);db.Users.Add(u);await db.SaveChangesAsync();
 }
 startup.ErrorType=null;startup.ErrorNumber=null;startup.Step="ready";startup.Ready=true;return;
 }catch(Exception ex){var cause=ex.GetBaseException();startup.ErrorType=cause.GetType().Name;startup.ErrorNumber=cause is Microsoft.Data.SqlClient.SqlException sql?sql.Number:null;app.Logger.LogWarning("MOZ startup failed at {Step}, type {Type}, SQL {Number}",startup.Step,startup.ErrorType,startup.ErrorNumber);if(attempt<4)await Task.Delay(TimeSpan.FromSeconds(attempt*2));}
 }
 startup.Failed=true;
}));
var api=app.MapGroup("/api");
api.MapPost("/login",async(LoginInput input,MozDb db,IPasswordHasher<User> hasher,HttpContext c)=>{
 var login=input.Login.Trim().ToLowerInvariant();if(Engine.Digits(login).Length==11&&!login.Contains('@'))login=Engine.Digits(login);
 var u=await db.Users.SingleOrDefaultAsync(x=>x.Login==login&&x.Active);
 if(u==null||u.Role is not ("global" or "operator" or "client" or "driver")||hasher.VerifyHashedPassword(u,u.PasswordHash,input.Password)==PasswordVerificationResult.Failed)return Results.Unauthorized();
 if(u.TenantId is {} tid&&!await db.Tenants.AnyAsync(x=>x.Id==tid&&x.Active))return Results.Unauthorized();
 var token=new JwtSecurityToken(issuer,audience,new[]{new Claim("sub",u.Id.ToString()),new Claim("role",u.Role)},expires:DateTime.UtcNow.AddHours(12),signingCredentials:new(signing,SecurityAlgorithms.HmacSha256));
 c.Response.Cookies.Append("moz_session",new JwtSecurityTokenHandler().WriteToken(token),new CookieOptions{HttpOnly=true,Secure=true,SameSite=SameSiteMode.Strict,MaxAge=TimeSpan.FromHours(12),Path="/"});
 return Results.Ok(Api.PublicUser(u));
}).RequireRateLimiting("auth");
api.MapPost("/register-driver",async(DriverInput x,MozDb db,IPasswordHasher<User> hasher)=>{
 var cpf=Engine.Digits(x.Cpf);var phone=Engine.Digits(x.Phone);
 if(!Engine.ValidCpf(cpf)||phone.Length is <10 or >13||x.Name.Trim().Length<3||x.Password.Length<10||x.Password.Length>128)return Results.BadRequest(new{error="Confira nome, CPF, celular e senha de pelo menos 10 caracteres."});
 if(await db.Users.AnyAsync(u=>u.Cpf==cpf))return Results.Conflict(new{error="Cadastro existente. Acesse sua conta."});
 var u=new User{Role="driver",Login=cpf,Cpf=cpf,Phone=phone,Name=x.Name.Trim(),IdentityVerified=false};u.PasswordHash=hasher.HashPassword(u,x.Password);db.Users.Add(u);await db.SaveChangesAsync();return Results.Ok(new{message="Cadastro recebido. A identidade deve ser conferida pela administração do MOZ antes da liberação da viagem."});
}).RequireRateLimiting("auth");
api.MapPost("/logout",(HttpContext c)=>{c.Response.Cookies.Delete("moz_session",new CookieOptions{Path="/",Secure=true,SameSite=SameSiteMode.Strict});return Results.Ok();});
var auth=api.MapGroup("").RequireAuthorization();
auth.MapMozEndpoints(storage,cfg);auth.MapPushEndpoints();
app.MapHub<MozHub>("/hubs/events").RequireAuthorization();
app.Run();
public record LoginInput(string Login,string Password);
public record DriverInput(string Name,string Cpf,string Phone,string Password);
public class MozHub(MozDb db):Hub {
 public override async Task OnConnectedAsync(){
 if(!Guid.TryParse(Context.User?.FindFirstValue("sub"),out var id)){Context.Abort();return;}
 var u=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.Active);
 if(u==null||u.Role is not ("global" or "operator" or "client" or "driver")||(u.TenantId is {} tid&&!await db.Tenants.AnyAsync(t=>t.Id==tid&&t.Active))){Context.Abort();return;}
 if(u.Role=="operator"&&u.TenantId is {} tenant)await Groups.AddToGroupAsync(Context.ConnectionId,"operator:"+tenant);
 else if(u.Role=="client"&&u.ClientId is {} client&&u.TenantId is {} ct)await Groups.AddToGroupAsync(Context.ConnectionId,"client:"+ct+":"+client);
 else if(u.Role=="driver"&&u.IdentityVerified)await Groups.AddToGroupAsync(Context.ConnectionId,"driver:"+u.Id);
 await base.OnConnectedAsync();
 }
}
public static class MozRealtime{
 public static async Task Publish(IHubContext<MozHub> hub,Trip t){
 var groups=new List<string>{"operator:"+t.TenantId,"client:"+t.TenantId+":"+t.ClientId};
 if(t.DriverId is {} driver)groups.Add("driver:"+driver);
 await hub.Clients.Groups(groups).SendAsync("changed");
 }
}

public class StartupState { public volatile bool Ready; public volatile bool Failed; public volatile string Step="starting"; public string? ErrorType; public int? ErrorNumber; }

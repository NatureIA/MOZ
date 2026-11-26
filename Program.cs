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
b.Services.AddHostedService<MonitorWorker>();
b.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o=>o.MultipartBodyLengthLimit=24*1024*1024);
var app=b.Build();
app.UseExceptionHandler(e=>e.Run(async c=>{c.Response.StatusCode=500;await c.Response.WriteAsJsonAsync(new{error="Falha interna. Consulte o administrador."});}));
app.UseHsts();app.UseHttpsRedirection();
app.Use(async(c,next)=>{
 c.Response.Headers["X-Content-Type-Options"]="nosniff";c.Response.Headers["Referrer-Policy"]="no-referrer";c.Response.Headers["X-Frame-Options"]="DENY";
 if((c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/hubs"))&&!startup.Ready){c.Response.StatusCode=503;await c.Response.WriteAsJsonAsync(new{error="MOZ inicializando ou indisponível."});return;}
 if(c.Request.Path.StartsWithSegments("/api")){c.Response.Headers.CacheControl="no-store";if((c.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE") && c.Request.Headers["X-MOZ-Request"]!="1"){c.Response.StatusCode=403;return;}}
 await next();
});
app.UseDefaultFiles();app.UseStaticFiles();app.UseRouting();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
var storage=Path.GetFullPath(cfg["Storage:RootPath"]??"../private/documents",app.Environment.ContentRootPath);
var webroot=Path.GetFullPath(app.Environment.WebRootPath??Path.Combine(app.Environment.ContentRootPath,"wwwroot"));
app.MapGet("/health",()=>Results.Ok(new{app="MOZ",status=startup.Ready?"ok":startup.Failed?"error":"initializing",database=startup.Ready?"ready":startup.Failed?"error":"waiting",version="0.1.1",databaseStep=startup.Step,databaseErrorType=startup.ErrorType,databaseErrorNumber=startup.ErrorNumber}));
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
 if(u==null||hasher.VerifyHashedPassword(u,u.PasswordHash,input.Password)==PasswordVerificationResult.Failed)return Results.Unauthorized();
 if(u.TenantId is {} tid&&!await db.Tenants.AnyAsync(x=>x.Id==tid&&x.Active))return Results.Unauthorized();
 var token=new JwtSecurityToken(issuer,audience,new[]{new Claim("sub",u.Id.ToString()),new Claim("role",u.Role)},expires:DateTime.UtcNow.AddHours(12),signingCredentials:new(signing,SecurityAlgorithms.HmacSha256));
 c.Response.Cookies.Append("moz_session",new JwtSecurityTokenHandler().WriteToken(token),new CookieOptions{HttpOnly=true,Secure=true,SameSite=SameSiteMode.Strict,MaxAge=TimeSpan.FromHours(12),Path="/"});
 return Results.Ok(Api.PublicUser(u));
}).RequireRateLimiting("auth");
api.MapPost("/register-driver",async(DriverInput x,MozDb db,IPasswordHasher<User> hasher)=>{
 var cpf=Engine.Digits(x.Cpf);var phone=Engine.Digits(x.Phone);
 if(!Engine.ValidCpf(cpf)||phone.Length is <10 or >13||x.Name.Trim().Length<3||x.Password.Length<10||x.Password.Length>128)return Results.BadRequest(new{error="Confira nome, CPF, celular e senha de pelo menos 10 caracteres."});
 if(await db.Users.AnyAsync(u=>u.Cpf==cpf))return Results.Conflict(new{error="Cadastro existente. Acesse sua conta."});
 var u=new User{Role="driver",Login=cpf,Cpf=cpf,Phone=phone,Name=x.Name.Trim(),IdentityVerified=false};u.PasswordHash=hasher.HashPassword(u,x.Password);db.Users.Add(u);await db.SaveChangesAsync();return Results.Ok(new{message="Cadastro recebido. A identidade deve ser verificada pelo operador antes da liberação da viagem."});
}).RequireRateLimiting("auth");
api.MapPost("/logout",(HttpContext c)=>{c.Response.Cookies.Delete("moz_session",new CookieOptions{Path="/",Secure=true,SameSite=SameSiteMode.Strict});return Results.Ok();});
var auth=api.MapGroup("").RequireAuthorization();
auth.MapMozEndpoints(storage,cfg);
app.MapHub<MozHub>("/hubs/events").RequireAuthorization();
app.Run();
public record LoginInput(string Login,string Password);
public record DriverInput(string Name,string Cpf,string Phone,string Password);
public class MozHub:Hub {}

public class StartupState { public volatile bool Ready; public volatile bool Failed; public volatile string Step="starting"; public string? ErrorType; public int? ErrorNumber; }

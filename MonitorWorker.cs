using Microsoft.EntityFrameworkCore;
namespace Moz;
public class MonitorWorker(IServiceScopeFactory scopeFactory,ILogger<MonitorWorker> logger,StartupState startup):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken ct){
 while(!ct.IsCancellationRequested){
 try{await Task.Delay(TimeSpan.FromSeconds(30),ct);if(!startup.Ready)continue;using var s=scopeFactory.CreateScope();var db=s.ServiceProvider.GetRequiredService<MozDb>();var now=DateTimeOffset.UtcNow;
 var trips=await db.Trips.Where(t=>t.Started!=null&&t.Finished==null&&t.Status!="Cancelada").Take(1000).ToListAsync(ct);
 foreach(var t in trips){
 if(!t.GapAlerted&&(now-(t.LastFix??t.Started!.Value)).TotalSeconds>t.GapSeconds){t.GapAlerted=true;db.Events.Add(Engine.Event(t,"Perda de comunicação","Sem posição GPS válida no intervalo configurado","Sistema",now));}
 var stage=await db.Stages.FirstOrDefaultAsync(x=>x.TripId==t.Id&&x.Order==t.CurrentStage,ct);
 if(stage?.Arrived is {} at&&stage.Departed==null&&stage.SlaMinutes>0&&!stage.SlaAlerted&&(now-at).TotalMinutes>stage.SlaMinutes){stage.SlaAlerted=true;db.Events.Add(Engine.Event(t,"SLA excedido","Permanência superior ao limite na etapa "+stage.Label,"Sistema",now,stage:stage));}
 }
 await db.SaveChangesAsync(ct);
 }catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}catch(Exception ex){logger.LogWarning("Monitor cycle failed: {Type}",ex.GetType().Name);}
 }
 }
}

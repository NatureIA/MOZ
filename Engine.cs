using System.Text.Json;
namespace Moz;
public static class Engine {
 public static string Digits(string? x)=>new string((x??"").Where(c=>c>='0'&&c<='9').ToArray());
 public static string? NormalizeTripReference(string? value){
 var reference=value?.Trim();return string.IsNullOrEmpty(reference)||reference.Length>160||reference.Any(char.IsControl)?null:reference;
 }
 public static bool SameTripReference(string? existing,string reference)=>!string.IsNullOrWhiteSpace(existing)&&string.Equals(existing.Trim(),reference.Trim(),StringComparison.OrdinalIgnoreCase);
 public static bool ValidCpf(string x) {
 if(x.Length!=11||x.Distinct().Count()==1) return false;
 for(int n=9;n<=10;n++){int s=0;for(int i=0;i<n;i++)s+=(x[i]-'0')*(n+1-i);int d=11-s%11;if(d>=10)d=0;if(x[n]-'0'!=d)return false;}return true;
 }
 public static bool ValidPoint(double lat,double lon)=>double.IsFinite(lat)&&double.IsFinite(lon)&&Math.Abs(lat)<=90&&Math.Abs(lon)<=180;
 public static double Distance(double a,double b,double c,double d){double r=Math.PI/180;double h=Math.Pow(Math.Sin((c-a)*r/2),2)+Math.Cos(a*r)*Math.Cos(c*r)*Math.Pow(Math.Sin((d-b)*r/2),2);return 6371000*2*Math.Asin(Math.Sqrt(Math.Clamp(h,0,1)));}
 public static bool ValidPolygon(string? json){if(string.IsNullOrWhiteSpace(json))return true;try{var p=JsonSerializer.Deserialize<double[][]>(json);return p!=null&&p.Length>=3&&p.Length<=500&&p.All(x=>x!=null&&x.Length==2&&ValidPoint(x[0],x[1]));}catch{return false;}}
 public static bool Contains(double lat,double lon,double centerLat,double centerLon,double radius,string? polygon,double margin=0){
 if(string.IsNullOrWhiteSpace(polygon))return Distance(lat,lon,centerLat,centerLon)<=radius+margin;
 var p=JsonSerializer.Deserialize<double[][]>(polygon)!;bool inside=false;for(int i=0,j=p.Length-1;i<p.Length;j=i++){if((p[i][0]>lat)!=(p[j][0]>lat)&&lon<(p[j][1]-p[i][1])*(lat-p[i][0])/(p[j][0]-p[i][0])+p[i][1])inside=!inside;}return inside;
 }
 public static bool ValidRoute(string? json){if(string.IsNullOrWhiteSpace(json))return false;try{var p=JsonSerializer.Deserialize<double[][]>(json);return p!=null&&p.Length>=2&&p.Length<=10000&&p.All(x=>x!=null&&x.Length==2&&ValidPoint(x[0],x[1]));}catch{return false;}}
 public static double RouteDistance(double lat,double lon,string json){var pts=JsonSerializer.Deserialize<double[][]>(json)!;double best=double.MaxValue;double k=Math.Cos(lat*Math.PI/180)*111320;for(int i=1;i<pts.Length;i++){double ax=(pts[i-1][1]-lon)*k,ay=(pts[i-1][0]-lat)*111320,bx=(pts[i][1]-lon)*k,by=(pts[i][0]-lat)*111320,dx=bx-ax,dy=by-ay;double den=dx*dx+dy*dy,t=den==0?0:Math.Clamp(-(ax*dx+ay*dy)/den,0,1);best=Math.Min(best,Math.Sqrt(Math.Pow(ax+t*dx,2)+Math.Pow(ay+t*dy,2)));}return best;}
 public static void AuditRoute(Trip t,TripPlan? plan,Fix f,MozDb db){if(plan==null||!f.Accepted||Terminal(t))return;double d=RouteDistance(f.Lat,f.Lon,plan.Coordinates);if(d>plan.ToleranceMetres+f.Accuracy){plan.OutsideCount++;plan.InsideCount=0;if(!plan.Outside&&plan.OutsideCount>=3){plan.Outside=true;db.Events.Add(Event(t,"Desvio de rota",$"Posição fora do corredor planejado: {d:F0} m","Inferido",f.At));}}else if(d<=plan.ToleranceMetres){plan.OutsideCount=0;plan.InsideCount++;if(plan.Outside&&plan.InsideCount>=3){plan.Outside=false;db.Events.Add(Event(t,"Retorno à rota","Posições novamente compatíveis com o corredor planejado","Inferido",f.At));}}}
 public static EventLog Event(Trip t,string kind,string message,string source,DateTimeOffset? at=null,Guid? actor=null,Stage? stage=null)=>new(){TripId=t.Id,StageId=stage?.Id,ActorId=actor,Kind=kind,Message=message,Source=source,At=at??DateTimeOffset.UtcNow,Lat=t.LastLat,Lon=t.LastLon};
 public static bool Terminal(Trip t)=>t.Status is "Finalizada" or "Cancelada";
 public static bool TrackingActive(Trip t)=>t.TrackingSince!=null&&t.Finished==null&&!Terminal(t);
 public static bool FixAuthorized(Trip t,DateTimeOffset? epoch,DateTimeOffset at)=>TrackingActive(t)&&epoch==t.TrackingSince&&at>=t.TrackingSince!.Value;
 public static void Advance(Trip t,Stage s,int count,MozDb db,DateTimeOffset at) {
 t.CurrentStage++;
 if(t.CurrentStage>=count){t.Status="Finalizada";t.Finished=at;t.TrackingSince=null;db.Events.Add(Event(t,"Finalizada","Viagem finalizada","Sistema",at));}
 else {t.Status="Em trânsito";db.Events.Add(Event(t,"Em trânsito","Deslocamento para próxima etapa","Sistema",at));}
 }
 public static void Process(Trip t,Stage? stage,int count,Fix f,MozDb db) {
 if(!f.Accepted)return;
 double? observedSpeed=f.Speed; double speed=observedSpeed??0;
 if(t.LastFix is {} last && t.TrackingSince is {} since && last>=since && t.LastLat is {} la && t.LastLon is {} lo) {
 var elapsed=(f.At-last).TotalSeconds;var metres=Distance(la,lo,f.Lat,f.Lon);
 if(elapsed>0&&elapsed<=t.GapSeconds&&metres/elapsed<55){
 if(observedSpeed==null){speed=metres/elapsed*3.6;observedSpeed=speed;}
 if(metres>Math.Max(5,f.Accuracy/2))t.DistanceKm+=metres/1000;
 if(speed>5||metres/elapsed>1.4)t.MovingSeconds+=elapsed;else t.IdleSeconds+=elapsed;
 } else if(elapsed>t.GapSeconds){db.Events.Add(Event(t,"Lacuna GPS","Intervalo sem posições válidas","Sistema",f.At));t.StopSince=null;t.StopAlerted=false;if(stage!=null){stage.InsideCount=0;stage.OutsideCount=0;stage.FirstInside=null;stage.FirstOutside=null;}}
 }
 t.LastFix=f.At;t.LastLat=f.Lat;t.LastLon=f.Lon;t.MaxSpeed=Math.Max(t.MaxSpeed,speed);
 if(t.GapAlerted){t.GapAlerted=false;db.Events.Add(Event(t,"Comunicação restabelecida","Posição recebida","GPS",f.At));}
 if(speed>t.SpeedLimit && !db.Events.Any(x=>x.TripId==t.Id&&x.Kind=="Excesso de velocidade"&&x.At>f.At.AddMinutes(-5)))db.Events.Add(Event(t,"Excesso de velocidade",$"Velocidade registrada: {speed:F0} km/h","GPS",f.At));
 if(observedSpeed!=null&&speed<=3){if(t.StopSince==null){t.StopSince=f.At;t.StopLat=f.Lat;t.StopLon=f.Lon;}
 else if(t.StopLat is {} slat&&t.StopLon is {} slon&&Distance(slat,slon,f.Lat,f.Lon)>Math.Max(30,f.Accuracy*2)){t.StopSince=f.At;t.StopLat=f.Lat;t.StopLon=f.Lon;t.StopAlerted=false;}
 if(!t.StopAlerted&&(f.At-t.StopSince.Value).TotalSeconds>=t.StopSeconds){t.StopAlerted=true;db.Events.Add(Event(t,"Parada","Parada detectada por GPS","Inferido",t.StopSince));}}
 else if(observedSpeed!=null) {if(t.StopAlerted)db.Events.Add(Event(t,"Retomada","Movimento retomado","GPS",f.At));t.StopSince=null;t.StopAlerted=false;}
 if(stage==null)return;
 bool inside=Contains(f.Lat,f.Lon,stage.Lat,stage.Lon,stage.Radius,stage.Polygon);
 bool outside=!Contains(f.Lat,f.Lon,stage.Lat,stage.Lon,stage.Radius,stage.Polygon,Math.Max(15,f.Accuracy));
 if(stage.Arrived==null){if(inside){stage.FirstInside??=f.At;stage.InsideCount++;if(stage.InsideCount>=2){stage.Arrived=stage.FirstInside;t.Status="No local: "+stage.Label;db.Events.Add(Event(t,"Chegada","Chegada à etapa "+stage.Label,"GPS",stage.Arrived,stage:stage));}}else{stage.InsideCount=0;stage.FirstInside=null;}return;}
 if(outside){stage.FirstOutside??=f.At;stage.OutsideCount++;if(stage.OutsideCount>=2&&stage.Departed==null){stage.Departed=stage.FirstOutside;db.Events.Add(Event(t,"Saída","Saída da etapa "+stage.Label,"GPS",stage.Departed,stage:stage));if(stage.RequirePod&&!db.Pods.Any(p=>p.StageId==stage.Id)){t.Status="Aguardando comprovante";}else Advance(t,stage,count,db,stage.Departed.Value);}}
 else{stage.OutsideCount=0;stage.FirstOutside=null;}
 }
}


// Stop audit is reconstructed from accepted GPS evidence, not from unpaired alert events.
public sealed record GpsStop(DateTimeOffset Start,DateTimeOffset LastEvidence,double DurationSeconds,double Lat,double Lon,double Accuracy,Guid? StageId,string? StageLabel,string State);
public sealed class GpsStopAudit(Trip trip,IReadOnlyList<Stage> stages){
 readonly List<GpsStop> stops=new();
 Fix? previous,first,stationary;
 double? previousSpeed;
 readonly Dictionary<Guid,DateTimeOffset> lastInside=new();
 public DateTimeOffset? LastInside(Guid stageId)=>lastInside.TryGetValue(stageId,out var at)?at:null;
 bool completed;
 void Close(string state){
 if(first!=null&&stationary!=null){
 var duration=(stationary.At-first.At).TotalSeconds;
 if(duration>=trip.StopSeconds){
 var stage=stages.FirstOrDefault(s=>s.Arrived is {} arrived&&arrived<=stationary.At&&(s.Departed==null||s.Departed>=first.At)&&Engine.Contains(first.Lat,first.Lon,s.Lat,s.Lon,s.Radius,s.Polygon));
 stops.Add(new GpsStop(first.At,stationary.At,duration,first.Lat,first.Lon,first.Accuracy,stage?.Id,stage?.Label,state));
 }}
 first=null;stationary=null;
 }
 public void Add(Fix f){
 if(completed)throw new InvalidOperationException("Audit already completed");
 if(!f.Accepted||!Engine.ValidPoint(f.Lat,f.Lon))return;
 foreach(var s in stages){if(s.Arrived is {} arrived&&f.At>=arrived&&(s.Departed==null||f.At<=s.Departed)&&Engine.Contains(f.Lat,f.Lon,s.Lat,s.Lon,s.Radius,s.Polygon))lastInside[s.Id]=f.At;}
 if(previous!=null){
 if(f.At<=previous.At)return;
 if(f.TrackingSince!=previous.TrackingSince||(f.At-previous.At).TotalSeconds>trip.GapSeconds){Close(f.TrackingSince!=previous.TrackingSince?"Autorização interrompida":"Lacuna GPS");previous=null;previousSpeed=null;}
 }
 double? speed=f.Speed;
 if(speed==null&&previous!=null){
 var delta=(f.At-previous.At).TotalSeconds;
 var metres=Engine.Distance(previous.Lat,previous.Lon,f.Lat,f.Lon);
 speed=metres<=Math.Max(5,Math.Max(f.Accuracy,previous.Accuracy)/2)?0:metres/delta*3.6;
 }
 if(speed is <=3){
 if(first!=null&&Engine.Distance(first.Lat,first.Lon,f.Lat,f.Lon)>Math.Max(30,Math.Max(first.Accuracy,f.Accuracy)*2))Close("Deslocamento detectado");
 first??=(previous!=null&&f.Speed==null&&(previousSpeed is null or <=3)?previous:f);
 stationary=f;
 }else if(speed!=null)Close("Retomada do movimento");
 previous=f;previousSpeed=speed;
 }
 public IReadOnlyList<GpsStop> Complete(DateTimeOffset now){
 if(completed)return stops;
 var state=trip.Finished!=null||Engine.Terminal(trip)?"Viagem encerrada":!Engine.TrackingActive(trip)?"Rastreamento inativo":previous==null||previous.TrackingSince!=trip.TrackingSince?"Autorização interrompida":(now-previous.At).TotalSeconds>trip.GapSeconds?"Lacuna GPS":"Em andamento";
 Close(state);completed=true;return stops;
 }
}

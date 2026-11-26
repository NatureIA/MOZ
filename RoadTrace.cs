using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Moz;

// Road matching is a presentation aid. Never replaces original evidence or audit data.
public sealed class RoadTrace(IConfiguration cfg,ILogger<RoadTrace> log):IDisposable {
 static Uri? Base(IConfiguration cfg){
 var raw=cfg["Routing:MatchBaseUrl"];
 if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)return null;
 return uri;
 }
 public static bool Configured(IConfiguration cfg)=>Base(cfg)!=null;
 readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(8)};
 readonly SemaphoreSlim slots=new(2);
 readonly ConcurrentDictionary<string,(DateTimeOffset At,double[][][] Lines)> cache=new();
 public static List<List<Fix>> DenseSegments(IReadOnlyList<Fix> points,int gap){
 var segments=new List<List<Fix>>();var current=new List<Fix>();
 foreach(var p in points){
 if(!p.Accepted||!Engine.ValidPoint(p.Lat,p.Lon)||!double.IsFinite(p.Accuracy)||p.Accuracy<0){if(current.Count>=3)segments.Add(current);current=new();continue;}
 if(current.Count>0){var prev=current[^1];var dt=(p.At-prev.At).TotalSeconds;var metres=Engine.Distance(prev.Lat,prev.Lon,p.Lat,p.Lon);
 if(dt<=0||dt>Math.Min(gap,30)||metres>350||metres/dt>55||p.TrackingSince!=prev.TrackingSince){if(current.Count>=3)segments.Add(current);current=new();}}
 current.Add(p);
 }
 if(current.Count>=3)segments.Add(current);
 return segments.Where(s=>Engine.Distance(s[0].Lat,s[0].Lon,s[^1].Lat,s[^1].Lon)>30||s.Zip(s.Skip(1),(x,y)=>Engine.Distance(x.Lat,x.Lon,y.Lat,y.Lon)).Sum()>60).ToList();
 }
 public async Task<double[][][]> Match(IReadOnlyList<Fix> points,int gap,CancellationToken ct){
 var uri=Base(cfg);if(uri==null)return Array.Empty<double[][]>();
 var result=new List<double[][]>();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(14));var token=deadline.Token;
 foreach(var segment in DenseSegments(points,gap)){
 if(token.IsCancellationRequested)break;
 string Num(double d)=>d.ToString("0.0000000",CultureInfo.InvariantCulture);
 var coords=string.Join(";",segment.Select(p=>Num(p.Lon)+","+Num(p.Lat)));
 var times=string.Join(";",segment.Select(p=>p.At.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
 var radii=string.Join(";",segment.Select(p=>Math.Clamp(p.Accuracy,5,50).ToString("0.0",CultureInfo.InvariantCulture)));
 var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri+"|"+coords+"|"+times+"|"+radii)));
 if(cache.TryGetValue(key,out var saved)&&DateTimeOffset.UtcNow-saved.At<TimeSpan.FromHours(6)){result.AddRange(saved.Lines);continue;}
 bool acquired=false;
 try{
 await slots.WaitAsync(token);acquired=true;
 var url=uri.AbsoluteUri.TrimEnd('/')+"/match/v1/driving/"+coords+"?geometries=geojson&overview=full&gaps=split&tidy=false&timestamps="+times+"&radiuses="+radii;
 using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);
 if(!response.IsSuccessStatusCode)continue;
 using var stream=await response.Content.ReadAsStreamAsync(token);
 using var body=await JsonDocument.ParseAsync(stream,cancellationToken:token);
 if(!body.RootElement.TryGetProperty("matchings",out var matches)||matches.ValueKind!=JsonValueKind.Array)continue;
 var lines=new List<double[][]>();
 foreach(var match in matches.EnumerateArray()){
 if(!match.TryGetProperty("confidence",out var confidence)||!confidence.TryGetDouble(out var conf)||conf<0.8)continue;
 if(!match.TryGetProperty("geometry",out var geometry)||!geometry.TryGetProperty("coordinates",out var coordinates)||coordinates.ValueKind!=JsonValueKind.Array)continue;
 var line=new List<double[]>();foreach(var c in coordinates.EnumerateArray()){
 if(c.ValueKind!=JsonValueKind.Array||c.GetArrayLength()<2||!c[0].TryGetDouble(out var lon)||!c[1].TryGetDouble(out var lat)||!Engine.ValidPoint(lat,lon)){line.Clear();break;}
 line.Add(new[]{lon,lat});if(line.Count>12000){line.Clear();break;}
 }
 if(line.Count>=2)lines.Add(line.ToArray());
 }
 if(cache.Count>=1024){foreach(var stale in cache.OrderBy(x=>x.Value.At).Take(128))cache.TryRemove(stale.Key,out _);}
 cache[key]=(DateTimeOffset.UtcNow,lines.ToArray());result.AddRange(lines);
 }catch(OperationCanceledException){break;}catch(Exception ex){log.LogWarning("Road matching unavailable: {Type}",ex.GetType().Name);}finally{if(acquired)slots.Release();}
 }
 return result.ToArray();
 }
 public void Dispose(){http.Dispose();slots.Dispose();}
}

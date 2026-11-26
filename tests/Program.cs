using Moz;
void Check(bool condition,string name){if(!condition)throw new Exception("Failed: "+name);Console.WriteLine("OK: "+name);}
Check(Engine.Distance(0,0,0,0)==0,"distance same point");
Check(Math.Abs(Engine.Distance(0,0,0,1)-111195)<100,"distance geodesic");
Check(Engine.Contains(0,0.0005,0,0,100,null),"inside 100m");
Check(!Engine.Contains(0,0.002,0,0,100,null),"outside 100m");
Check(Engine.Contains(0,0.001,0,0,100,null,20),"hysteresis");
Check(Engine.ValidPolygon("[[0,0],[0,1],[1,1],[1,0]]"),"polygon valid");
Check(Engine.Contains(.5,.5,0,0,100,"[[0,0],[0,1],[1,1],[1,0]]"),"polygon inside");
Check(!Engine.Contains(2,2,0,0,100,"[[0,0],[0,1],[1,1],[1,0]]"),"polygon outside");
Check(!Engine.ValidCpf("00000000000"),"CPF repeated invalid");
Check(Engine.ValidCpf("52998224725"),"CPF checksum example");
Check(!Engine.ValidPoint(91,0),"coordinate bounds");
Console.WriteLine("Geometry and input rules passed.");

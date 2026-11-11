using System;using System.IO;
class P{static int Main(string[] a){
 var b=@"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64";
 var cfg=Path.Combine(Path.GetTempPath(),"msbuild18.exe.config");
 File.Copy(Path.Combine(b,"MSBuild.exe.config.template"),cfg,true);
 var s=new AppDomainSetup{ApplicationBase=b,ConfigurationFile=cfg};
 var d=AppDomain.CreateDomain("msb",null,s);
 return d.ExecuteAssembly(Path.Combine(b,"MSBuild.exe"),a);}}

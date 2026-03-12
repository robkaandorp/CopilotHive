using System;
using System.Reflection;

var asm = Assembly.LoadFrom(@"C:\Users\rob\.nuget\packages\github.copilot.sdk\0.1.32\lib\net8.0\GitHub.Copilot.SDK.dll");
var t = asm.GetType("GitHub.Copilot.SDK.PermissionRequestHandler");
Console.WriteLine("Type: " + t);
Console.WriteLine("BaseType: " + t?.BaseType);
var invoke = t?.GetMethod("Invoke");
Console.WriteLine("ReturnType: " + invoke?.ReturnType);
foreach(var p in invoke?.GetParameters() ?? Array.Empty<ParameterInfo>()) 
    Console.WriteLine("  Param: " + p.ParameterType + " " + p.Name);

var resultType = asm.GetType("GitHub.Copilot.SDK.PermissionRequestResult");
Console.WriteLine("\nPermissionRequestResult properties:");
foreach(var prop in resultType?.GetProperties() ?? Array.Empty<PropertyInfo>())
    Console.WriteLine("  " + prop.PropertyType + " " + prop.Name);

var kindType = asm.GetType("GitHub.Copilot.SDK.PermissionRequestResultKind");
Console.WriteLine("\nPermissionRequestResultKind properties:");
foreach(var prop in kindType?.GetProperties(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<PropertyInfo>())
    Console.WriteLine("  " + prop.Name + " = " + prop.GetValue(null));

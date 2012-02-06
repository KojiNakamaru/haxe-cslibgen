using System;
using System.Text;
using System.Linq;
using Mono.Cecil;
using System.Collections.Generic;

namespace cslibgen
{
	class CsLibGen
	{
		public static string outputDir = "";
		public static List<string> assemblies = new List<string>();
		public static List<AssemblyDefinition> assemDefs = new List<AssemblyDefinition>();
		public static List<string> inputDirs = new List<string>();
		
		public static AssemblyDefinition curAssemDef;
		public static TypeDefinition curTypeDef;
		public static EventDefinition curEventDef;
		public static FieldDefinition curFieldDef;
		public static PropertyDefinition curPropDef;
		public static MethodDefinition curMethDef;
		public static Dictionary<string, List<TypeDefinition>> typesByBaseName;
		public static Dictionary<string, TypeReference> curImports;
		public static HashSet<string> curUsedTypeNames;
		public static string curTypeName;
		public static string curFullTypeName;
		public static string curNsPath;
		public static string curFilePath;
		public static string curFileName;
		
		public static HashSet<string> haxeKeywords = new HashSet<string> {
			"function", "class", "static", "var", "if", "else", "while", "do", "for", "break", "return", "continue",
			"extends", "implements", "import", "switch", "case", "default", "private", "public", "try", "catch",
			"new", "this", "throw", "extern", "enum", "in", "interface", "untyped", "cast", "override", "typedef",
			"dynamic", "package", "callback", "inline", "using"
		};
		
		public static System.IO.StreamWriter os;
		
		public static int Main (string[] args)
		{
			
			// Write usage output.
			if (args.Length == 0) {
				Console.WriteLine("Haxe C# library bindings generator\n" +
				                   "  Generates Haxe bindings for C# assemblies\n" +
				                   "  Usage: cslibgen -o <outputdir> -i <inputdir> <assembly> [<assembly> ..]\n" +
				                   "  Options:\n" + 
				                   "  -o The output folder into which the bindings will be placed.\n" +
				                   "  -i An input directory from which to load assemblies.\n");
				return 1;
			}
			
			// Parse command line arguments.
			int i = 0;
			while (i < args.Length) {
				if (args[i] == "-o") {
					outputDir = args[i + 1];
					i += 2;
				} else if (args[i] == "-i") {
					inputDirs.Add(args[i + 1]);
					i += 2;
				} else {
					assemblies.Add(args[i]);
					i++;
				}
			}
			
			// Check if we have a valid output dir.
			if (String.IsNullOrEmpty(outputDir)) {
				Console.WriteLine("You must specify an output folder.");
				return 1;
			}
			
			// Check if we have any assemblies.
			if (assemblies.Count == 0) {
				Console.WriteLine("You must specify at least one assembly to output.");
				return 1;
			}
			
			//
			// Create output dir if it doesn't already exist.
			//
			
			System.IO.Directory.CreateDirectory(outputDir);
			
			typesByBaseName = new Dictionary<string, List<TypeDefinition>>();

			//
			// Find and load all assemblies..
			//
			
			foreach (var assemblyName in assemblies) {

				curAssemDef = null;
				
				if (System.IO.Path.IsPathRooted(assemblyName)) {
					curAssemDef = AssemblyDefinition.ReadAssembly(assemblyName);
				} else {
					foreach (var inputDir in inputDirs) {
						var assemPath = System.IO.Path.Combine(inputDir, assemblyName);
						if (System.IO.File.Exists(assemPath)) {
							try {
								curAssemDef = AssemblyDefinition.ReadAssembly(assemPath);
							} catch (Exception e) {
								Console.WriteLine(e.Message);
								return 1;
							}
						}
					}
				}
				
				if (curAssemDef == null) {
					Console.WriteLine("Unable to find assembly " + assemblyName + "!");
					return 1;
				}
				
				assemDefs.Add(curAssemDef);
			}
			
			//
			// Now process all types in all assemblies to ensure that all type names are unique.
			//
			
			foreach (var assemDef in assemDefs) {
				
				curAssemDef = assemDef;

				// Compile a list of all non unique type base names (i.e. Tuple<>, Tuple<,>, Tuple<,,>).  
				// We use this list to convert these type names using a number suffix later.
				foreach (var typeDef in curAssemDef.MainModule.Types) {
					if (typeDef.IsPublic) {
						List<TypeDefinition> typeList;
						var nuBaseName = GetNonUniqueFullTypeBaseName(typeDef);
						if (!typesByBaseName.TryGetValue(nuBaseName, out typeList)) {
							typeList = new List<TypeDefinition>();
							typesByBaseName[nuBaseName] = typeList;
						}
						
						typeList.Add(typeDef);
					}
				}
			}
			
			//
			// Output all public types to files.
			//
			
			foreach (var assemDef in assemDefs) {
				
				curAssemDef = assemDef;
				
				// Now create the actual haxe binding file for each public type.
				foreach (var typeDef in curAssemDef.MainModule.Types) {
					curTypeDef = typeDef;
					
					if (curTypeDef.IsPublic) {
						WriteTypeDef(typeDef);
					}
				}		
				
			}
			
			return 0;
		}
		
		// Basically returns the namespace + type name (minus any generic `1 suffixes).
		public static string GetTypeBaseName (TypeReference typeRef)
		{
			var p = typeRef.Name.IndexOf ('`');
			if (p != -1) {
				return typeRef.Name.Substring (0, p);
			} else {
				return typeRef.Name;
			}
		}		
		
		// Basically returns the namespace + type name (minus any generic `1 suffixes).
		public static string GetNonUniqueFullTypeBaseName (TypeReference typeRef)
		{
			return typeRef.Namespace + "." + GetTypeBaseName (typeRef);
		}
		
		// Gets the full .net type name with or without type param names (i.e. List<T> or Tuple<,,>).
		public static string GetDotNetFullTypeName (TypeReference typeRef, bool withTypeParamNames = false)
		{
			var sb = new StringBuilder ();
			sb.Append (GetTypeBaseName(typeRef));
			if (typeRef.GenericParameters != null && typeRef.GenericParameters.Count > 0) {
				sb.Append ("<");
				bool first = true;
				foreach (var param in typeRef.GenericParameters) {
					if (!first) {
						sb.Append (",");
					}
					if (withTypeParamNames) {
						sb.Append (param.Name);
					}
					first = false;
				}
				sb.Append (">");
			}
			return sb.ToString ();
		}
		
		// Gets a guaranteed unique simple type name (by adding number indexes for non-unique generic types).
		public static string GetFinalTypeBaseName (TypeReference typeRef)
		{
			List<TypeDefinition> typeList;
			var nuBaseName = GetNonUniqueFullTypeBaseName(typeRef);
			if (typesByBaseName.TryGetValue(nuBaseName, out typeList)) {
				if (typeList.Count > 1) {
					var genericInst = typeRef as GenericInstanceType;
					if (typeRef.GenericParameters.Count > 0) {
						return GetTypeBaseName(typeRef) + typeRef.GenericParameters.Count.ToString();
					} else if (typeList.Count > 0 && genericInst != null) {
						return GetTypeBaseName(typeRef) + genericInst.GenericArguments.Count.ToString();
					}
				}
			}
			
			return GetTypeBaseName(typeRef);
		}
		
		public static string GetFullFinalTypeName (TypeReference typeRef)
		{
			var sb = new StringBuilder ();
			sb.Append (GetFinalTypeBaseName (typeRef));
			if (typeRef.GenericParameters != null && typeRef.GenericParameters.Count > 0) {
				sb.Append ("<");
				bool first = true;
				foreach (var param in typeRef.GenericParameters) {
					if (!first) {
						sb.Append (",");
					}
					sb.Append (param.Name);
					first = false;
				}
				sb.Append (">");
			}
			return sb.ToString();
		}
		
		public static void WriteTypeDef (TypeDefinition typeDef)
		{
			curTypeName = GetFinalTypeBaseName(typeDef);
			curFullTypeName = GetFullFinalTypeName(typeDef);
			curNsPath = curTypeDef.Namespace.ToLower().Replace(".", System.IO.Path.DirectorySeparatorChar.ToString());
			curFilePath = System.IO.Path.Combine(outputDir, curNsPath);
			curFileName = System.IO.Path.Combine(curFilePath, curTypeName + ".hx");
			
			System.IO.Directory.CreateDirectory(curFilePath);

			curImports = new Dictionary<string, TypeReference>();
			
			ResetUsedTypeNames();
		
			var sw = new System.IO.StringWriter();
			
			// Make extends string
			var extends = typeDef.BaseType != null ? 
				" extends " + MakeTypeName(typeDef.BaseType, true) : "";
			
			// Make implements string
			var implementsList = curTypeDef.Interfaces.Count > 0 ?
				(!String.IsNullOrEmpty(extends) ? "," : "") + " implements " + 
					String.Join(", implements ", typeDef.Interfaces.Select((arg) => MakeTypeName(arg))) : "";
			
			// Make class/interface declaration
			if (curTypeDef.IsEnum) {
				
				//
				// We're an enum
				//

				sw.Write("@:fakeEnum(" + MakeTypeName(GetEnumUnderlyingType(typeDef)) + 
				          ") @:native(\"" + curTypeDef.Namespace + "." + GetDotNetFullTypeName(curTypeDef) + "\")\n" +
				          "extern enum " + curFullTypeName + " {\n");
				
				foreach (var fieldDef in typeDef.Fields) {
					if (fieldDef.IsStatic && fieldDef.Name != "__value") {
						sw.Write("\t" + fieldDef.Name + ";\n");
					}
				}
				
				sw.WriteLine("}");				
				
			} else {
				
				//
				// We're an interface or class
				//
				
				sw.Write("@:native(\"" + curTypeDef.Namespace + "." + GetDotNetFullTypeName(curTypeDef) + "\")");
				
				if (curTypeDef.IsSealed && !curTypeDef.IsInterface) {
					sw.Write(" @:final");
				}
				
				sw.Write("\n");
				
				sw.Write("extern " + (curTypeDef.IsInterface ? "interface " : "class ") + curFullTypeName + extends + implementsList + " {\n");
				
				//
				// Write Event Definitions
				//
				
				foreach (var eventDef in curTypeDef.Events) {
					
					curEventDef = eventDef;
					
					if (eventDef.AddMethod.IsPublic) {
					
						sw.Write("\tpublic " + (eventDef.AddMethod.IsStatic ? "static " : "") + 
						         "var " + eventDef.Name + "(default,null) : cs.NativeEvent<system.EventArgs>;\n");
					}

				}
				
				curEventDef = null;				
				
				//
				// Write Field Definitions
				//
				
					foreach (var fieldDef in curTypeDef.Fields) {
					
						curFieldDef = fieldDef;
					
						if (fieldDef.IsPublic) {
					
							sw.Write("\tpublic " + (fieldDef.IsStatic ? "static " : "") + 
						         "var " + fieldDef.Name + " : " + 
						         MakeTypeName(fieldDef.FieldType) + ";\n");
						}

					}
				
				curFieldDef = null;
				
				//
				// Write Property Definitions
				//
				
				foreach (var propDef in curTypeDef.Properties) {
					
					curPropDef = propDef;
					
					var getter = propDef.GetMethod;
					var setter = propDef.SetMethod;
					
					if (((getter != null && getter.IsPublic && !IsOverridenMethod(getter)) ||
					     (setter != null && setter.IsPublic && !IsOverridenMethod(setter))) && 
					    !propDef.HasParameters) {
					
						if (getter != null && getter.IsPublic &&
						    setter != null && setter.IsPublic) {
							
							sw.Write("\t" + MakeMethodAttributes(getter ?? setter) 
						         + "var " + propDef.Name + " : " +
						         MakeTypeName(propDef.PropertyType) + ";\n");
							
						} else {
						
							sw.Write("\t" + MakeMethodAttributes(getter ?? setter) 
							         + "var " + propDef.Name + "(" + 
							         (getter != null && getter.IsPublic ? "default" : "null") + "," +
							         (setter != null && setter.IsPublic ? "default" : "null") + ") : " +
							         MakeTypeName(propDef.PropertyType) + ";\n");

						}
					}

				}
				
				curPropDef = null;
				
				//
				// Write Method Definitions
				//
				
				// First collect and sort methods by unique name..

				var uniqueMethods = new Dictionary<string, List<Tuple<string, string, string, MethodDefinition>>>();
				
				foreach (var methodDef in curTypeDef.Methods) {
					curMethDef = methodDef;
					
					if (methodDef.IsPublic && !IsOverridenMethod(methodDef) && !methodDef.Name.StartsWith("op_") &&
					    !methodDef.IsGetter && !methodDef.IsSetter && !methodDef.IsRemoveOn && !methodDef.IsAddOn) {
						
						List<Tuple<string,string,string,MethodDefinition>> methList;
						if (!uniqueMethods.TryGetValue(methodDef.Name, out methList)) {
							methList = new List<Tuple<string,string,string,MethodDefinition>>();
							uniqueMethods[methodDef.Name] = methList;
						}
						
						string methodAttrs = MakeMethodAttributes(methodDef);
						string methodName;
						string methodDecl;

						if (methodDef.IsConstructor) {
							
							methodName = "new";
							methodDecl = "(" + MakeMethodParams(methodDef) + ") : Void";
							
						} else {
							
							methodName = methodDef.Name;
							methodDecl = "(" + MakeMethodParams(methodDef) + ") : " +
							          MakeTypeName(methodDef.ReturnType);
							
						} 
						
						methList.Add(new Tuple<string,string,string,MethodDefinition>(methodAttrs, methodName, methodDecl, methodDef));
					}
				}
				
				curMethDef = null;
				
				// Now write out each unique method with all of it's overloads..
				
				var sortedMethods = uniqueMethods.OrderBy((arg) => arg.Key).ToList();
				
				foreach (var methodDefPair in sortedMethods) {
					
					var methList = methodDefPair.Value.OrderByDescending((arg) => arg.Item4.Parameters.Count).ThenBy((arg) => arg.Item3).ToList();
					
					sw.WriteLine();

					for (int i = 0; i < methList.Count; i++) {
						if (i < methList.Count - 1) {
							sw.Write("\t@:overload(function" + methList[i].Item3 + " {})\n");
						} else {
							sw.Write("\t" + methList[i].Item1 + "function " + methList[i].Item2 + methList[i].Item3 + ";\n");
						}
					}
				}
				
				sw.WriteLine("}");
			
			}
			
			os = new System.IO.StreamWriter(curFileName);
			
			os.Write("package " + curTypeDef.Namespace.ToLower() + ";\n\n");
			
//			var sortedRefs = curImports.ToList().OrderBy((arg) => arg.Key);
//			
//			foreach (var typeRefPair in sortedRefs) {
//				var typeRef = typeRefPair.Value;
//				os.WriteLine("import " + typeRef.Namespace.ToLower() + "." + GetFinalTypeBaseName(typeRef) + ";");				
//			}
//			
//			os.WriteLine();
			
			os.Write(sw.GetStringBuilder().ToString());
			
			os.Close();
		}
		
		public static bool IsOverridenMethod(MethodDefinition methodDef)
		{
			return (methodDef.IsVirtual && !methodDef.IsNewSlot);
		}
		
		public static void ResetUsedTypeNames()
		{
			curUsedTypeNames = new HashSet<string>();
			string[] names = {"String", "Bool", "Int", "UInt", "Dynamic", "Array", "Void", "Class", "Date", "StringBuf", 
							  "DateTools", "Enum", "EReg", "Hash", "IntHash", "IntIter", "Lambda", "List", 
							  "Math", "Reflect", "Std", "Iterator", "Iterable", "ArrayAccess", "Type", "XmlType", "Xml"};
			foreach (var n in names) {
				curUsedTypeNames.Add (n);
			}
		}
		
		public static string MakeMethodAttributes (MethodDefinition methodDef)
		{
			var sb = new StringBuilder();
			if (methodDef.IsPublic && !methodDef.DeclaringType.IsInterface) {
				sb.Append("public ");
			}
			if (methodDef.IsStatic) {
				sb.Append("static ");
			}
			return sb.ToString();
		}
		
		public static string MakeTypeName (TypeReference typeRef, bool useExactType = false)
		{
			if (typeRef.IsGenericParameter) {
				
				// Handle generic methods with arrays with type parameters..
				if (curMethDef != null && curMethDef.HasGenericParameters && typeRef.IsGenericParameter && 
				    curMethDef.GenericParameters.FirstOrDefault((arg) => arg.Name == typeRef.Name) != null) {
					return "Dynamic";
				}
				
				return typeRef.Name;
			}
			
			if (typeRef.IsByReference || typeRef.IsPointer) {
				return MakeTypeName(typeRef.GetElementType());
			}
			
			if (typeRef is ArrayType) {
				var arrayType = typeRef as ArrayType;
				
				// Handle generic methods with arrays with type parameters..
				if (curMethDef != null && curMethDef.HasGenericParameters && arrayType.ElementType.IsGenericParameter && 
				    curMethDef.GenericParameters.FirstOrDefault((arg) => arg.Name == arrayType.ElementType.Name) != null) {

					// Must be an array type (C# will automatically fill in the type parameter based on the array).
					return "system.Array";
				}
				
				return "cs.NativeArray" + 
					(arrayType.Dimensions.Count > 1 ? arrayType.Dimensions.Count.ToString() : "") + 
						"<" + MakeTypeName(arrayType.ElementType, true) + ">";
			}
			
			if (useExactType) {
				switch (typeRef.FullName) {
				case "System.String": 
					return "String";
				case "System.Boolean": 
					return "Bool";
				case "System.Double": 
					return "Float";
				case "System.Int32":
					return "Int";
				case "System.UInt32":
					return "UInt";
				}
			} else {
				switch (typeRef.FullName) {
				case "System.Void":
					return "Void";
				case "System.Object":
					return "Dynamic";
				case "System.String": 
					return "String";
				case "System.Boolean": 
					return "Bool";
				case "System.Single":
				case "System.Double": 
					return "Float";
				case "System.SByte":
				case "System.Int16":
				case "System.Int32":
					return "Int";
				case "System.Byte":
				case "System.UInt16":
				case "System.UInt32":
					return "UInt";
				}
			}
			
			var typeBaseName = GetFinalTypeBaseName(typeRef);
//			var fullTypeName = typeRef.Namespace + "." + typeBaseName;
			
//			// Add this type to the imports list..
//			if (fullTypeName != curFullTypeName) {
//				curImports[fullTypeName] = typeRef;
//			}
			
			// Make full type name (including generic params).
			var sb = new StringBuilder();
			sb.Append(typeRef.Namespace.ToLower() + "." + typeBaseName);
			if (typeRef.IsGenericInstance) {
				var genericInst = typeRef as GenericInstanceType;
				sb.Append("<");
				bool first = true;
				foreach (var genericType in genericInst.GenericArguments) {
					if (!first) {
						sb.Append(",");
					}
					if (genericType.IsGenericParameter) {
						
						// If we're a method with generic parameters, we have to figure out how to make a parameter Haxe will allow.
						if (curMethDef != null) {
							if (curMethDef.HasGenericParameters &&
							    curMethDef.GenericParameters.FirstOrDefault((arg) => arg.Name == genericType.Name) != null) {
								if (typeRef.Namespace == "System.Collections.Generic" && typeBaseName == "IEnumerable") {
									return "system.collections.IEnumerable"; // Anything that implements IEnumerable<T> always implements IEnumerable.
								} else if (typeRef.Namespace == "System.Collections.Generic" && typeBaseName == "IComparer") {
									return "system.collections.IComparer"; // Anything that implements IEnumerable<T> always implements IEnumerable.
								} else if (typeRef.Namespace == "System" && typeBaseName == "IComparable") {
									return "system.IComparable"; // Anything that implements IComparable<T> always implements IComparable.
								} else {
									// We'll just have to punt generic method's generic parameters to "Dynamic" if we can't use a non
									// parameterized type above.
									return "Dynamic";
								}
							}
						}
							
						sb.Append(genericType.Name);
					} else {
						sb.Append(MakeTypeName(genericType, true));
					}
					first = false;
				}
				sb.Append(">");
			}
			
			return sb.ToString();			
		}
		
		public static string MakeParamName (string name)
		{
			if (haxeKeywords.Contains(name)) {
				return "_" + name;
			} else {
				return name;
			}
		}
		
		public static string MakeMethodParams (MethodDefinition methodDef)
		{
			var sb = new StringBuilder ();
			var first = true;
			foreach (var paramDef in methodDef.Parameters) {
				if (!first) {
					sb.Append (", ");
				}
				sb.Append (MakeParamName(paramDef.Name));
				sb.Append (":");
				sb.Append (MakeTypeName (paramDef.ParameterType));
				first = false;
			}
			return sb.ToString ();
		}
		
		public static TypeReference GetEnumUnderlyingType (TypeDefinition typeDef)
		{ 
			if (!typeDef.IsEnum) 
				throw new ArgumentException (); 
			var fields = typeDef.Fields; 
			for (int i = 0; i < fields.Count; i++) { 
				var field = fields [i]; 
				if (!field.IsStatic) 
					return field.FieldType; 
			} 
			throw new ArgumentException (); 
		} 
		
	}
}

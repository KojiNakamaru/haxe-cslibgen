using System;
using System.Text;
using System.Linq;
using Mono.Cecil;
using System.Collections.Generic;

namespace cslibgen {
  class CsLibGen
  {
    public static string outputDir = "";
    public static List<string> assemblies = new List<string>();
    public static Dictionary<string, TypeDefinition> allTypes = new Dictionary<string, TypeDefinition>();
    public static List<AssemblyDefinition> assemDefs = new List<AssemblyDefinition>();
    public static List<string> inputDirs = new List<string>();
    public static Dictionary<string, List<TypeDefinition>> typesByBaseName;
    public static Dictionary<string, TypeReference> curImports;
    public static HashSet<string> curUsedTypeNames;
    public static MethodDefinition curMethDef;
    public static string curTypeName;
    public static string curFullTypeName;
    public static string curNs;
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

    public static int Main(string[] args) {

      // Write usage output.
      if ( args.Length == 0 ) {
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
      while ( i < args.Length ) {
        if ( args[i] == "-o" ) {
          outputDir = args[i + 1];
          i += 2;
        } else if ( args[i] == "-i" ) {
          inputDirs.Add(args[i + 1]);
          i += 2;
        } else {
          assemblies.Add(args[i]);
          i++;
        }
      }

      // Check if we have a valid output dir.
      if ( String.IsNullOrEmpty(outputDir) ) {
        Console.WriteLine("You must specify an output folder.");
        return 1;
      }

      // Check if we have any assemblies.
      if ( assemblies.Count == 0 ) {
        Console.WriteLine("You must specify at least one assembly to output.");
        return 1;
      }

      var ret = 1;

      //try {
        ret = GenerateLibs();
      //} catch ( Exception e ) {
      //  Console.WriteLine("ERROR: " + e.Message);
      //}

      return ret;
    }

    public static int GenerateLibs() {

      //
      // Create output dir if it doesn't already exist.
      //

      System.IO.Directory.CreateDirectory(outputDir);

      typesByBaseName = new Dictionary<string, List<TypeDefinition>>();

      //
      // Find and load all assemblies..
      //

      foreach ( var assemblyName in assemblies ) {

        AssemblyDefinition curAssemDef = null;

        if ( System.IO.Path.IsPathRooted(assemblyName) ) {
          curAssemDef = AssemblyDefinition.ReadAssembly(assemblyName);
        } else {
          foreach ( var inputDir in inputDirs ) {
            var assemPath = System.IO.Path.Combine(inputDir, assemblyName);
            if ( System.IO.File.Exists(assemPath) ) {
              try {
                curAssemDef = AssemblyDefinition.ReadAssembly(assemPath);
              } catch ( Exception e ) {
                Console.WriteLine("Error loading assembly " + assemPath + " - " + e.Message);
                return 1;
              }
            }
          }
        }

        if ( curAssemDef == null ) {
          Console.WriteLine("Unable to find assembly " + assemblyName + "!");
          return 1;
        }

        assemDefs.Add(curAssemDef);
      }

      //
      // Now process all types in all assemblies to ensure that all type names are unique.
      //

      foreach ( var assemDef in assemDefs ) {
        // Compile a list of all non unique type base names (i.e. Tuple<>, Tuple<,>, Tuple<,,>).
        // We use this list to convert these type names using a number suffix later.
        foreach ( var typeDef in assemDef.MainModule.Types ) {
          allTypes[typeDef.FullName] = typeDef;
          if ( typeDef.IsPublic ) {
            List<TypeDefinition> typeList;
            var nuBaseName = GetNonUniqueFullTypeBaseName(typeDef);
            if ( !typesByBaseName.TryGetValue(nuBaseName, out typeList) ) {
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

      foreach ( var assemDef in assemDefs ) {

        // Now create the actual haxe binding file for each public type.
        foreach ( var module in assemDef.Modules ) {
          foreach ( var typeDef in module.Types ) {
            if ( typeDef.IsPublic ) { // && typeDef.FullName == "System.TimeZoneInfo") {
              Console.WriteLine(typeDef.FullName);
              WriteTopLevelTypeDef(typeDef);
            }
          }
        }
      }

      return 0;
    }

    public static TypeDefinition GetTypeDef(TypeReference typeRef) {
      TypeDefinition typeDef = null;
      allTypes.TryGetValue(typeRef.FullName, out typeDef);
      return typeDef;
    }

    // Returns the base type name (minus namespace and any generic `1 suffixes).
    public static string GetTypeBaseName(TypeReference typeRef) {
      var p = typeRef.Name.IndexOf('`');
      if ( p != -1 ) {
        return typeRef.Name.Substring(0, p);
      } else {
        return typeRef.Name;
      }
    }

    // Basically returns the namespace + type name (minus any generic `1 suffixes).
    public static string GetNonUniqueFullTypeBaseName(TypeReference typeRef) {
      var sb = new StringBuilder();
      if ( typeRef.Namespace != null && typeRef.Namespace.Length > 0) {
        sb.Append(typeRef.Namespace);
        sb.Append(".");
      }

      if ( typeRef.DeclaringType != null ) {
        sb.Append(GetNonUniqueFullTypeBaseName(typeRef.DeclaringType));
        sb.Append(".");
      }

      sb.Append(GetTypeBaseName(typeRef));
      return sb.ToString();
    }

    // Gets the full .net type name with or without type param names (i.e. List<T> or Tuple<,,>).
    public static string GetDotNetFullTypeName(TypeReference typeRef, bool withTypeParamNames = false) {
      var sb = new StringBuilder();
      sb.Append(GetTypeBaseName(typeRef));
      if ( typeRef.GenericParameters != null && typeRef.GenericParameters.Count > 0 ) {
        sb.Append("<");
        bool first = true;
        foreach ( var param in typeRef.GenericParameters ) {
          if ( !first ) {
            sb.Append(",");
          }
          if ( withTypeParamNames ) {
            sb.Append(param.Name);
          }
          first = false;
        }
        sb.Append(">");
      }
      return sb.ToString();
    }

    // Gets a guaranteed unique simple type name (by adding number indexes for non-unique generic types).
    public static string GetFinalTypeBaseName(TypeReference typeRef) {
      List<TypeDefinition> typeList;
      var nuBaseName = GetNonUniqueFullTypeBaseName(typeRef);
      if ( typesByBaseName.TryGetValue(nuBaseName, out typeList) ) {
        if ( typeList.Count > 1 ) {
          var genericInst = typeRef as GenericInstanceType;
          if ( typeRef.GenericParameters.Count > 0 ) {
            return GetTypeBaseName(typeRef) + typeRef.GenericParameters.Count.ToString();
          } else if ( typeList.Count > 0 && genericInst != null ) {
            return GetTypeBaseName(typeRef) + genericInst.GenericArguments.Count.ToString();
          }
        }
      }

      var sb = new StringBuilder();
      if ( typeRef.DeclaringType != null ) {
        sb.Append(GetFinalTypeBaseName(typeRef.DeclaringType) + "_");
      }

      sb.Append(GetTypeBaseName(typeRef));
      return sb.ToString();
    }

    public static string GetGenericParameters(TypeReference typeRef) {
      var sb = new StringBuilder();
      if ( typeRef.GenericParameters != null && typeRef.GenericParameters.Count > 0 ) {
        sb.Append("<");
        bool first = true;
        foreach ( var param in typeRef.GenericParameters ) {
          if ( !first ) {
            sb.Append(",");
          }
          sb.Append(param.Name);
          first = false;
        }
        sb.Append(">");
      }
      return sb.ToString();
    }

    public static string GetFullFinalTypeName(TypeReference typeRef) {
      var sb = new StringBuilder();
      sb.Append(GetFinalTypeBaseName(typeRef));
      sb.Append(GetGenericParameters(typeRef));
      return sb.ToString();
    }

    public static string GetUnadornedMethodName(MethodDefinition methodDef) {
      var name = methodDef.Name;
      int idx;
      if ( (idx = name.LastIndexOf(".")) >= 0 ) {
        name = name.Substring(idx+1);
      }
      return name;
    }

    public static void WriteTopLevelTypeDef(TypeDefinition typeDef) {
      curNs = typeDef.Namespace;
      curNsPath = typeDef.Namespace.ToLower().Replace(".", System.IO.Path.DirectorySeparatorChar.ToString());
      curFilePath = System.IO.Path.Combine(outputDir, curNsPath);
      curTypeName = GetFinalTypeBaseName(typeDef);
      curFileName = System.IO.Path.Combine(curFilePath, curTypeName + ".hx");

      System.IO.Directory.CreateDirectory(curFilePath);

      curImports = new Dictionary<string, TypeReference>();

      ResetUsedTypeNames();

      var sw = new System.IO.StringWriter();
      WriteTypeDef(typeDef, sw);

      os = new System.IO.StreamWriter(curFileName);

      os.Write("package " + typeDef.Namespace.ToLower() + ";\n\n");

      //      var sortedRefs = curImports.ToList().OrderBy((arg) => arg.Key);
      //
      //      foreach (var typeRefPair in sortedRefs) {
      //        var typeRef = typeRefPair.Value;
      //        os.WriteLine("import " + typeRef.Namespace.ToLower() + "." + GetFinalTypeBaseName(typeRef) + ";");
      //      }
      //
      //      os.WriteLine();

      os.Write(sw.GetStringBuilder().ToString());

      os.Close();
    }

    public static void WriteTypeDef(TypeDefinition typeDef, System.IO.StringWriter sw) {
      curFullTypeName = GetFullFinalTypeName(typeDef);

      // Make extends string
      var baseTypeDef = typeDef.BaseType != null ? GetTypeDef(typeDef.BaseType) : null;
      var extends = baseTypeDef != null ?
        " extends " + MakeTypeName(baseTypeDef, true) : "";

      // Make implements string
      var publicInterfaces = typeDef.Interfaces.Where((arg) => GetTypeDef(arg) != null && GetTypeDef(arg).IsPublic).ToList();
      var implementsList = publicInterfaces.Count > 0 ?
        (!String.IsNullOrEmpty(extends) ? "," : "") + " implements " +
          String.Join(", implements ", publicInterfaces.Select((arg) => MakeTypeName(arg))) : "";

      // Make class/interface declaration
      if ( typeDef.IsEnum ) {

        //
        // We're an enum
        //

        sw.Write("@:fakeEnum(" + MakeTypeName(GetEnumUnderlyingType(typeDef)) +
                 ") @:native(\"" + GetNonUniqueFullTypeBaseName(typeDef) + "\")\n" +
                 "extern enum " + curFullTypeName + " {\n");

        foreach ( var fieldDef in typeDef.Fields ) {
          if ( fieldDef.IsStatic && fieldDef.Name != "__value" ) {
            sw.Write("\t" + fieldDef.Name + ";\n");
          }
        }

        sw.WriteLine("}\n");

      } else {


        // Write out all nested classes first
        var ourTypeName = curFullTypeName;
        foreach ( var nestedType in typeDef.NestedTypes ) {
          if ( nestedType.IsNestedPublic ) {
            curFullTypeName = GetFinalTypeBaseName(nestedType);
            WriteTypeDef(nestedType.Resolve(), sw);
          }
        }
        curFullTypeName = ourTypeName;

        //
        // We're an interface or class
        //

        sw.Write("@:native(\"" + GetNonUniqueFullTypeBaseName(typeDef) + "\")");

        if ( typeDef.IsSealed && !typeDef.IsInterface ) {
          sw.Write(" @:final");
        }

        sw.Write("\n");

        sw.Write("extern " + (typeDef.IsInterface ? "interface " : "class ") + curFullTypeName + extends + implementsList + " {\n");

        //
        // Write Event Definitions
        //

        foreach ( var eventDef in typeDef.Events ) {

          var eventArgType = GetEventArgType(eventDef.EventType);

          if ( eventArgType != null ) {

            if ( eventDef.AddMethod.IsPublic ) {

              sw.Write("\tpublic " + (eventDef.AddMethod.IsStatic ? "static " : "") +
                       "var " + eventDef.Name + "(default,null) : cs.system.NativeEvent<" + MakeTypeName(eventArgType) + ">;\n");
            }
          }

        }

        //
        // Write Field Definitions
        //

        foreach ( var fieldDef in typeDef.Fields ) {

          if ( fieldDef.IsPublic ) {

            sw.Write("\tpublic " + (fieldDef.IsStatic ? "static " : "") +
                     "var " + fieldDef.Name + " : " +
                     MakeTypeName(fieldDef.FieldType) + ";\n");
          }

        }

        //
        // Write Property Definitions
        //

        foreach ( var propDef in typeDef.Properties ) {

          var getter = propDef.GetMethod;
          var setter = propDef.SetMethod;

          if ( ((getter != null && getter.IsPublic && !IsHiddenOverride(getter)) ||
                (setter != null && setter.IsPublic && !IsHiddenOverride(setter))) &&
              !propDef.HasParameters ) {

            if ( getter != null && getter.IsPublic &&
                setter != null && setter.IsPublic ) {

              sw.Write("\t" + MakeMethodAttributes(getter ?? setter)
                       + "var " + propDef.Name + " : " +
                       MakeTypeName(propDef.PropertyType) + ";\n");

            } else {

              sw.Write("\t@:skipReflection" + MakeMethodAttributes(getter ?? setter)
                       + "var " + propDef.Name + "(" +
                       (getter != null && getter.IsPublic ? "default" : "never") + "," +
                       (setter != null && setter.IsPublic ? "default" : "never") + ") : " +
                       MakeTypeName(propDef.PropertyType) + ";\n");

            }
          }

        }

        //
        // Write Method Definitions
        //

        // First collect and sort instance methods by unique name..

        var uniqueMethods = new Dictionary<string, List<Tuple<string, string, string, MethodDefinition>>>();

        foreach ( var methodDef in typeDef.Methods ) {
          curMethDef = methodDef;
          var methodName = GetUnadornedMethodName(methodDef);
          if ( !methodDef.IsStatic && (methodDef.IsPublic || methodDef.IsVirtual) &&
              !IsHiddenOverride(methodDef) && !methodDef.Name.StartsWith("op_") &&
              !methodDef.IsGetter && !methodDef.IsSetter && !methodDef.IsRemoveOn && !methodDef.IsAddOn ) {

            List<Tuple<string,string,string,MethodDefinition>> methList;
            if ( !uniqueMethods.TryGetValue(methodName, out methList) ) {
              methList = new List<Tuple<string,string,string,MethodDefinition>>();
              uniqueMethods[methodName] = methList;
            }

            string methodAttrs = MakeMethodAttributes(methodDef);
            string methodDecl;

            if ( methodDef.IsConstructor ) {

              methodName = "new";
              methodDecl = "(" + MakeMethodParams(methodDef) + ") : Void";

            } else {

              methodDecl = "(" + MakeMethodParams(methodDef) + ") : " +
                MakeTypeName(methodDef.ReturnType);
            }

            methList.Add(new Tuple<string,string,string,MethodDefinition>(methodAttrs, methodName, methodDecl, methodDef));
          }
        }

        var uniqueStaticMethods = new Dictionary<string, List<Tuple<string, string, string, MethodDefinition>>>();
        var requiresStaticClass = false;
        // Iterate over static methods
        foreach ( var methodDef in typeDef.Methods ) {
          curMethDef = methodDef;
          var methodName = GetUnadornedMethodName(methodDef);
          if ( methodDef.IsStatic && methodDef.IsPublic &&
              !methodDef.Name.StartsWith("op_") &&
              !methodDef.IsGetter && !methodDef.IsSetter && !methodDef.IsRemoveOn && !methodDef.IsAddOn ) {

            // static methods need to be genericized separately
            if ( typeDef.HasGenericParameters ) {
              methodName += GetGenericParameters(typeDef);
            }

            List<Tuple<string,string,string,MethodDefinition>> methList;
            if ( !uniqueStaticMethods.TryGetValue(methodName, out methList) ) {
              methList = new List<Tuple<string,string,string,MethodDefinition>>();
              uniqueStaticMethods[methodName] = methList;

              if ( uniqueMethods.ContainsKey(methodName) ) {
                requiresStaticClass = true;
              }

              string methodAttrs = MakeMethodAttributes(methodDef);
              string methodDecl = "(" + MakeMethodParams(methodDef) + ") : " +
                  MakeTypeName(methodDef.ReturnType);

              methList.Add(new Tuple<string,string,string,MethodDefinition>(methodAttrs, methodName, methodDecl, methodDef));
            }
          }
        }

        curMethDef = null;

        if ( !requiresStaticClass ) {
          foreach ( var entry in uniqueStaticMethods ) {
            uniqueMethods[entry.Key] = entry.Value;
          }
        }

        // Now write out each unique method with all of it's overloads..
        writeMethods(uniqueMethods, sw);

        sw.WriteLine("}\n");

        if ( requiresStaticClass ) {
          // We have name conflicts between static and instance methods (allowed in C#, disallowed in Haxe)
          // Create a new class with just the static methods
          sw.Write("\n@:native(\"" + GetNonUniqueFullTypeBaseName(typeDef) + "\")");
          sw.Write(" @:final");

          sw.Write("\n");

          sw.Write("extern class " + curFullTypeName + "_Static {\n");

          writeMethods(uniqueStaticMethods, sw);
          sw.WriteLine("}\n");
        }
      }
    }

    public static void writeMethods(Dictionary<string, List<Tuple<string, string, string, MethodDefinition>>> uniqueMethods, System.IO.StringWriter sw) {
      var sortedMethods = uniqueMethods.OrderBy((arg) => arg.Key).ToList();

      foreach ( var methodDefPair in sortedMethods ) {

        var methList = methodDefPair.Value.OrderByDescending((arg) => arg.Item4.Parameters.Count).ThenByDescending((arg) => arg.Item3).ToList();

        sw.WriteLine();

        if ( methList.Count > 1 ) {
          for ( int i = 0; i < methList.Count; ++i ) {
            if ( !methList[i].Item4.IsPublic ) {
              methList.RemoveAt(i); --i;
            }
          }
        }

        for ( int i = 0; i < methList.Count; i++ ) {
          if ( i < methList.Count - 1 ) {
            sw.Write("\t@:overload(function" + methList[i].Item3 + " {})\n");
          } else {
            sw.Write("\t" + methList[i].Item1 + "function " + methList[i].Item2 + methList[i].Item3 + ";\n");
          }
        }
      }
    }

    public static TypeReference GetEventArgType(TypeReference handlerRef) {
      var handlerDef = GetTypeDef(handlerRef);
      if ( handlerDef != null ) {
        var invokeMethod = handlerDef.Methods.SingleOrDefault((arg) => arg.Name == "Invoke");
        if ( invokeMethod != null && invokeMethod.Parameters.Count == 2 ) {
          return invokeMethod.Parameters[1].ParameterType;
        }
      }
      return null;
    }

    public static bool IsHiddenOverride(MethodDefinition methodDef) {
      if (methodDef.IsConstructor) return false;

      var appearsInBase = MethodDeclaredInBase(methodDef);
      if ( appearsInBase ) return true; // FIXME: need to handle functions that share a name with a base-class function, but have different arguments.

      if ( methodDef.IsVirtual ) {
        var appearsInItf = methodDef.DeclaringType.Interfaces.Any((itf) => {
          return MethodDeclaredInInterface(itf.Resolve(), methodDef);
        });
        if ( appearsInBase ) {
          return true;
        }
        return !appearsInItf;
      } else {
        return false;
      }
    }

    public static bool MethodDeclaredInInterface(TypeDefinition itf, MethodDefinition methodDef) {
      var methodName = GetUnadornedMethodName(methodDef);
      foreach ( var itfMethod in itf.Methods ) {
        if ( GetUnadornedMethodName(itfMethod) == methodName ) {
          return true;
        }
      }

      foreach ( var itfRef in itf.Interfaces ) {
        if ( MethodDeclaredInInterface(itfRef.Resolve(), methodDef) ) {
          return true;
        }
      }

      return false;
    }

    public static bool MethodDeclaredInBase(MethodDefinition methodDef) {
      var type = methodDef.DeclaringType;
      var curBase = type.BaseType;
      var methodName = GetUnadornedMethodName(methodDef);
      while ( curBase != null ) {
        var resolved = curBase.Resolve();
        foreach ( var baseMethod in resolved.Methods ) {
          if ( GetUnadornedMethodName(baseMethod) == methodName ) {
            return true;
          }
        }
        curBase = resolved.BaseType;
      }
      return false;
    }

    public static void ResetUsedTypeNames() {
      curUsedTypeNames = new HashSet<string>();
      string[] names = {
        "String",
        "Bool",
        "Int",
        "UInt",
        "Dynamic",
        "Array",
        "Void",
        "Class",
        "Date",
        "StringBuf",
        "DateTools",
        "Enum",
        "EReg",
        "Hash",
        "IntHash",
        "IntIter",
        "Lambda",
        "List",
        "Math",
        "Reflect",
        "Std",
        "Iterator",
        "Iterable",
        "ArrayAccess",
        "Type",
        "XmlType",
        "Xml"
      };
      foreach ( var n in names ) {
        curUsedTypeNames.Add(n);
      }
    }

    public static string MakeMethodAttributes(MethodDefinition methodDef) {
      var sb = new StringBuilder();
      if ( methodDef.IsPublic && !methodDef.DeclaringType.IsInterface ) {
        sb.Append("public ");
      }
      if ( methodDef.IsStatic ) {
        sb.Append("static ");
      }
      if ( methodDef.IsVirtual && MethodDeclaredInBase(methodDef) ) {
        sb.Append("override ");
      }
      return sb.ToString();
    }

    public static string MakeTypeName(TypeReference typeRef, bool useExactType = false) {
      if ( typeRef.IsGenericParameter ) {

        // Handle generic methods with arrays with type parameters..
        if ( curMethDef != null && curMethDef.HasGenericParameters && typeRef.IsGenericParameter &&
          curMethDef.GenericParameters.FirstOrDefault((arg) => arg.Name == typeRef.Name) != null ) {
          return "Dynamic";
        }

        return typeRef.Name;
      }

      if ( typeRef.IsByReference || typeRef.IsPointer ) {
        return MakeTypeName(typeRef.GetElementType());
      }

      if ( typeRef is ArrayType ) {
        var arrayType = typeRef as ArrayType;

        // Handle generic methods with arrays with type parameters..
        if ( curMethDef != null && curMethDef.HasGenericParameters && arrayType.ElementType.IsGenericParameter &&
          curMethDef.GenericParameters.FirstOrDefault((arg) => arg.Name == arrayType.ElementType.Name) != null ) {

          // Must be an array type (C# will automatically fill in the type parameter based on the array).
          return "cs.system.Array";
        }

        return "cs.NativeArray" +
          (arrayType.Dimensions.Count > 1 ? arrayType.Dimensions.Count.ToString() : "") +
          "<" + MakeTypeName(arrayType.ElementType, true) + ">";
      }

      if ( useExactType ) {
        switch ( typeRef.FullName ) {
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
        case "System.Object":
          return "Dynamic";
        }
      } else {
        switch ( typeRef.FullName ) {
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
        case "System.IntPtr":
          return "cs.Pointer<Int>";
        }
      }

      var typeBaseName = GetFinalTypeBaseName(typeRef);
      //      var fullTypeName = typeRef.Namespace + "." + typeBaseName;

      //      // Add this type to the imports list..
      //      if (fullTypeName != curFullTypeName) {
      //        curImports[fullTypeName] = typeRef;
      //      }

      // Make full type name (including generic params).
      var sb = new StringBuilder();
      if ( typeRef.Namespace != null && typeRef.Namespace.Length > 0 && typeRef.Namespace != curNs ) {
        sb.Append("cs." + typeRef.Namespace.ToLower() + ".");
      }
      sb.Append(typeBaseName);

      if ( typeRef.IsGenericInstance ) {
        var genericInst = typeRef as GenericInstanceType;
        sb.Append("<");
        bool first = true;
        foreach ( var genericType in genericInst.GenericArguments ) {
          if ( !first ) {
            sb.Append(",");
          }
          if ( genericType.IsGenericParameter ) {

            // If we're a method with generic parameters, we have to figure out how to make a parameter Haxe will allow.
            if ( curMethDef != null ) {
              if ( curMethDef.HasGenericParameters &&
                  curMethDef.GenericParameters.FirstOrDefault((arg) => arg.Name == genericType.Name) != null ) {
                if ( typeRef.Namespace == "System.Collections.Generic" && typeBaseName == "IEnumerable" ) {
                  return "cs.system.collections.IEnumerable"; // Anything that implements IEnumerable<T> always implements IEnumerable.
                } else if ( typeRef.Namespace == "System.Collections.Generic" && typeBaseName == "IComparer" ) {
                  return "cs.system.collections.IComparer"; // Anything that implements IEnumerable<T> always implements IEnumerable.
                } else if ( typeRef.Namespace == "System" && typeBaseName == "IComparable" ) {
                  return "cs.system.IComparable"; // Anything that implements IComparable<T> always implements IComparable.
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

    public static string MakeParamName(string name) {
      if ( haxeKeywords.Contains(name) ) {
        return "_" + name;
      } else {
        return name;
      }
    }

    public static string MakeMethodParams(MethodDefinition methodDef) {
      var sb = new StringBuilder();
      var first = true;
      foreach ( var paramDef in methodDef.Parameters ) {
        if ( !first ) {
          sb.Append(", ");
        }
        sb.Append(MakeParamName(paramDef.Name));
        sb.Append(":");
        sb.Append(MakeTypeName(paramDef.ParameterType));
        first = false;
      }
      return sb.ToString();
    }

    public static TypeReference GetEnumUnderlyingType(TypeDefinition typeDef) {
      if ( !typeDef.IsEnum )
        throw new ArgumentException();
      var fields = typeDef.Fields;
      for ( int i = 0; i < fields.Count; i++ ) {
        var field = fields[i];
        if ( !field.IsStatic )
          return field.FieldType;
      }
      throw new ArgumentException();
    }

  }
}

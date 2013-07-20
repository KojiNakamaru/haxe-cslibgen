using System;
using System.Text;
using System.Linq;
using Mono.Cecil;
using System.Collections.Generic;


namespace cslibgen {
  using GenMethod = Tuple<string,string,string,MethodDefinition,bool>;

  class CsLibGen
  {
    public static string outputDir = "";
    public static bool skipPackagePrefix = false;
    public static string packageNameOverride= null;
    public static List<string> assemblies = new List<string>();
    public static Dictionary<string, TypeDefinition> allTypes = new Dictionary<string, TypeDefinition>();
    public static List<AssemblyDefinition> assemDefs = new List<AssemblyDefinition>();
    public static List<string> inputDirs = new List<string>();
    public static Dictionary<string, List<TypeDefinition>> typesByBaseName;
    public static Dictionary<string, TypeReference> curImports;
    public static HashSet<string> curUsedTypeNames;
    public static MethodDefinition curMethDef;
    public static string curTypeName;
    public static TypeReference curType;
    public static string curFullTypeName;
    public static string curNs;
    public static string curNsPath;
    public static string curFilePath;
    public static string curFileName;
    public static HashSet<String> rootPackages = new HashSet<string>();
    public static Dictionary<string,string> overriddenNamespaces = new Dictionary<string,string>();
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
                          "  -i An input directory from which to load assemblies.\n" +
                          "  -p Package name override source_package dest_package  [optional]\n" +
                          "  -s Skip 'dotnet' package prefix for all generated classes [optional] \n" +
                          "  -r Specify a root package name, i.e. skip 'dotnet' prefix for this package [optional] \n"
                          );
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
        } else if ( args[i] == "-p" ) {
          packageNameOverride = args[i + 1];
          i += 2;
        } else if ( args[i] == "-s" ) {
          skipPackagePrefix = true;
          i++;
        } else if ( args[i] == "-r" ) {
          rootPackages.Add(args[i+1].ToLower());
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
      // Add input dirs to assembly serach definition
      //


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
        var mainModule = assemDef.MainModule;
        // Tell the type resolveer to look in all specified include folders for external references
        foreach ( var inputDir in inputDirs ) {
          ((BaseAssemblyResolver)mainModule.AssemblyResolver).AddSearchDirectory(inputDir);
        }
        // Compile a list of all non unique type base names (i.e. Tuple<>, Tuple<,>, Tuple<,,>).
        // We use this list to convert these type names using a number suffix later.
        foreach ( var typeDef in mainModule.Types ) {
          allTypes[typeDef.FullName] = typeDef;
          if ( packageNameOverride != null || skipPackagePrefix ) {
            var newNS = packageNameOverride != null ? packageNameOverride : "";
            if ( typeDef.Namespace != null && typeDef.Namespace != "" ) {
             if ( packageNameOverride != null ) newNS += ".";
             newNS += typeDef.Namespace.ToLower();
            }
            overriddenNamespaces[typeDef.Namespace] = newNS;
          }
          foreach ( var nestedType in typeDef.NestedTypes ) {
            allTypes[nestedType.FullName] = nestedType;
          }
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
      // Output all public types to files assuming we don't want to ignore them.
      //
      var defsToIgnore = new HashSet<string> {
        "System.Type", "System.String"
      };

      foreach ( var assemDef in assemDefs ) {

        // Now create the actual haxe binding file for each public type.
        foreach ( var module in assemDef.Modules ) {
          foreach ( var typeDef in module.Types ) {
            if (!defsToIgnore.Contains(typeDef.FullName)) {
              if ( typeDef.IsPublic) { // && typeDef.FullName == "System.TimeZoneInfo") {
                Console.WriteLine(typeDef.FullName);
                WriteTopLevelTypeDef(typeDef);
              }
            }
          }
        }
      }

      return 0;
    }

    public static String GetTypeLookupName (TypeReference typeRef) {
      var name = new StringBuilder();
      if (typeRef.DeclaringType != null) {
        name.Append(GetTypeLookupName(typeRef.DeclaringType));
        name.Append("/");
      } else if ( typeRef.Namespace != null && typeRef.Namespace.Length > 0 ) {
        name.Append(typeRef.Namespace);
        name.Append(".");
      }
      name.Append(typeRef.Name);
      return name.ToString();
    }

    public static TypeDefinition GetTypeDef (TypeReference typeRef) {
      TypeDefinition typeDef = null;
      var fullName = GetTypeLookupName(typeRef);
      allTypes.TryGetValue(fullName, out typeDef);
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
      curType = typeDef;
      curFileName = System.IO.Path.Combine(curFilePath, curTypeName + ".hx");

      System.IO.Directory.CreateDirectory(curFilePath);

      curImports = new Dictionary<string, TypeReference>();

      ResetUsedTypeNames();

      var sw = new System.IO.StringWriter();
      WriteTypeDef(typeDef, sw);

      os = new System.IO.StreamWriter(curFileName);

      if (skipPackagePrefix) {
        os.Write("package " + typeDef.Namespace.ToLower() + ";\n\n");
      }
      else if(packageNameOverride != null) {
        string n = typeDef.Namespace == "" ? packageNameOverride : packageNameOverride + "." + typeDef.Namespace.ToLower();
        os.Write("package " + n + ";\n\n");
      }
      else {
        os.Write("package dotnet." + typeDef.Namespace.ToLower() + ";\n\n");
      }

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
      var extends = typeDef.BaseType != null ?
        " extends " + MakeTypeName(typeDef.BaseType, true) : "";

      // Make implements stringt
      var publicInterfaces = typeDef.Interfaces.Where((arg) => GetTypeDef(arg) == null || GetTypeDef(arg).IsPublic).ToList();
      var inherits = typeDef.IsInterface ? "extends" : "implements";
      var implementsList = publicInterfaces.Count > 0 ?
        (!String.IsNullOrEmpty(extends) ? " " : "") + " " + inherits + " " +
          String.Join(" " + inherits + " ", publicInterfaces.Select((arg) => MakeTypeName(arg))) : "";

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
            sw.Write("  " + fieldDef.Name + ";\n");
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

          int eventNumParams;
          var eventArgType = GetEventArgType(eventDef.EventType, out eventNumParams);
          if ( eventArgType != null ) {
            if ( eventDef.AddMethod.IsPublic ) {
              var eventClass = eventNumParams == 1 ? "dotnet.system.NativeEvent1" : "dotnet.system.NativeEvent";
              var typeName = MakeTypeName(eventArgType);
              sw.Write("  public " + (eventDef.AddMethod.IsStatic ? "static " : "") +
                       "var " + eventDef.Name + "(default,null) : " + eventClass + "<" + typeName + ">;\n");
            }
          }

        }

        //
        // Write Field Definitions
        //
        if ( !typeDef.IsInterface ) {
          foreach ( var fieldDef in typeDef.Fields ) {

            if ( fieldDef.IsPublic ) {
              sw.Write("  public " + (fieldDef.IsStatic ? "static " : "") +
                       "var " + fieldDef.Name + " : " +
                       MakeTypeName(fieldDef.FieldType) + ";\n");
            }

          }
        }
                
        //
        // Write Property Definitions
        //
        if ( !typeDef.IsInterface ) {
          foreach ( var propDef in typeDef.Properties ) {

            var getter = propDef.GetMethod;
            var setter = propDef.SetMethod;
            if ( (getter == null || MethodDeclaredInBase(getter, false)) &&
                 (setter == null || MethodDeclaredInBase(setter, false) ) ) {
              continue;
            }

            if ( ((getter != null && getter.IsPublic && !IsHiddenOverride(getter)) ||
                  (setter != null && setter.IsPublic && !IsHiddenOverride(setter))) &&
                !propDef.HasParameters ) {

              if ( getter != null && getter.IsPublic &&
                  setter != null && setter.IsPublic ) {

                sw.Write("  " + MakeMethodAttributes(getter ?? setter)
                         + "var " + propDef.Name + " : " +
                         MakeTypeName(propDef.PropertyType) + ";\n");

              } else {
                sw.Write("  " + MakeMethodAttributes(getter ?? setter)
                         + "var " + propDef.Name + "(" +
                         (getter != null && getter.IsPublic ? "default" : "never") + "," +
                         (setter != null && setter.IsPublic ? "default" : "never") + ") : " +
                         MakeTypeName(propDef.PropertyType) + ";\n");
              }
            }

          }
        }

        //
        // Write Method Definitions
        //

        // First collect and sort instance methods by unique name..

        var uniqueMethods = new Dictionary<string, List<GenMethod>>();

        foreach ( var methodDef in typeDef.Methods ) {
          curMethDef = methodDef;
          var methodName = GetUnadornedMethodName(methodDef);
          var appearsInItf = methodDef.DeclaringType.Interfaces.Any((itf) => {
            return MethodDeclaredInInterface(itf.Resolve(), methodDef);
          });

          if ( !methodDef.IsStatic && (methodDef.IsPublic || appearsInItf) &&
              !IsHiddenOverride(methodDef) && !methodDef.Name.StartsWith("op_") &&
              !methodName.StartsWith("get_") && !methodName.StartsWith("set_") &&
              !methodDef.IsGetter && !methodDef.IsSetter && !methodDef.IsRemoveOn && !methodDef.IsAddOn ) {

            List<GenMethod> methList;
            if ( !uniqueMethods.TryGetValue(methodName, out methList) ) {
              methList = new List<GenMethod>();
              uniqueMethods[methodName] = methList;
            }

            string methodDecl;
            bool forceOverride = false;

            if ( methodDef.IsConstructor ) {

              methodName = "new";
              methodDecl = "(" + MakeMethodParams(methodDef) + ") : Void";

            } else {

              methodDecl = "(" + MakeMethodParams(methodDef) + ") : " +
                MakeTypeName(methodDef.ReturnType);

              // If we're introducing a new override, include the previous override.
              if ( !MethodDeclaredInBase(methodDef, true, false) && MethodDeclaredInBase(methodDef, false, false) ) {
                var baseMethodDef = GetMethodDefInBase(methodDef);
                var baseMethodDecl = "(" + MakeMethodParams(baseMethodDef) + ") : " +
                  MakeTypeName(baseMethodDef.ReturnType);
                forceOverride = true;
                methList.Add(new GenMethod(MakeMethodAttributes(methodDef, true),methodName,baseMethodDecl,baseMethodDef,true));
              }
            }
            methList.Add(new GenMethod(MakeMethodAttributes(methodDef, forceOverride), methodName, methodDecl, methodDef,false));
          }
        }

        var uniqueStaticMethods = new Dictionary<string, List<GenMethod>>();
        var requiresStaticClass = false;

        Dictionary<string, int> instanceMethods = new Dictionary<string, int>();

        GetUniqueInstanceMethodNames(typeDef, instanceMethods);

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

            List<GenMethod> methList;
            if ( !uniqueStaticMethods.TryGetValue(methodName, out methList) ) {
              methList = new List<GenMethod>();
              uniqueStaticMethods[methodName] = methList;
            }

            if ( instanceMethods.ContainsKey(methodName) ) {
              requiresStaticClass = true;
            }

            string methodAttrs = MakeMethodAttributes(methodDef);
            string methodDecl = "(" + MakeMethodParams(methodDef) + ") : " +
              MakeTypeName(methodDef.ReturnType);

            methList.Add(new GenMethod(methodAttrs, methodName, methodDecl, methodDef, false));
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

    static void GetUniqueInstanceMethodNames(TypeDefinition typeDef, Dictionary<string,int> io_methods) {
      if ( typeDef.BaseType != null && GetTypeDef(typeDef.BaseType) != null) {
        GetUniqueInstanceMethodNames(GetTypeDef(typeDef.BaseType), io_methods);
      }
      var publicInterfaces = typeDef.Interfaces.Where((arg) => GetTypeDef(arg) != null && GetTypeDef(arg).IsPublic).ToList();
      foreach ( var itfDef in publicInterfaces ) {
        if ( GetTypeDef(itfDef) != null) {
          GetUniqueInstanceMethodNames(GetTypeDef(itfDef), io_methods);
        }
      }

      foreach ( var methodDef in typeDef.Methods ) {
        curMethDef = methodDef;
        var methodName = GetUnadornedMethodName(methodDef);
        if ( !methodDef.IsStatic && methodDef.IsPublic &&
             !IsHiddenOverride(methodDef) && !methodDef.Name.StartsWith("op_") &&
            !methodDef.IsGetter && !methodDef.IsSetter && !methodDef.IsRemoveOn && !methodDef.IsAddOn ) {
          io_methods[methodName] = 1;
        }
      }
    }

    public static void writeMethods(Dictionary<string, List<GenMethod>> uniqueMethods, System.IO.StringWriter sw) {
      var sortedMethods = uniqueMethods.OrderBy((arg) => arg.Key).ToList();

      foreach ( var methodDefPair in sortedMethods ) {

        var methList = methodDefPair.Value.OrderBy((arg) => arg.Item5 ? 1 : 0).ToList();
        sw.WriteLine();

        /*
        if ( methList.Count > 1 ) {
          for ( int i = 0; i < methList.Count; ++i ) {
            if ( !methList[i].Item4.IsPublic && !MethodDeclaredInBase(methList[i].Item4, false, false) ) {
              methList.RemoveAt(i); --i;
            }
          }
        }
        */

        var methods = new HashSet<string>();

        for ( int i = 0; i < methList.Count; i++ ) {
          if ( i < methList.Count - 1 ) {
            var line = "  @:overload(function" + methList[i].Item3 + " {})\n";
            if (!methods.Contains(line)) {
              methods.Add(line);
              sw.Write(line);
            }
          } else {
            sw.Write("  " + methList[i].Item1 + "function " + methList[i].Item2 + methList[i].Item3 + ";\n");
          }
        }
      }
    }

    public static TypeReference GetEventArgType (TypeReference handlerRef, out int numParams) {
      var handlerDef = GetTypeDef(handlerRef);
      if ( handlerDef != null ) {
        var invokeMethod = handlerDef.Methods.SingleOrDefault((arg) => arg.Name == "Invoke");
        if ( invokeMethod != null ) {
          numParams = invokeMethod.Parameters.Count;
          if ( invokeMethod.Parameters.Count == 1 || invokeMethod.Parameters.Count == 2 ) {
            var type = ( invokeMethod.Parameters.Count == 2 ) 
              ? invokeMethod.Parameters[1].ParameterType
              : invokeMethod.Parameters[0].ParameterType;
            // Handle generic delegates
            if ( type.IsGenericParameter ) {
              if ( handlerRef.IsGenericInstance && handlerDef.HasGenericParameters ) {
                var genType = (GenericInstanceType)handlerRef;

                var paramIndex = 0;
                foreach ( var param in handlerDef.GenericParameters ) {
                  if ( param.Name == type.Name ) {
                    break;
                  }
                  paramIndex++;
                }

                if ( paramIndex < handlerDef.GenericParameters.Count ) {
                  type = genType.GenericArguments[paramIndex];
                }
              }
            }
            return type;
          }
        }
      }
      numParams = -1;
      return null;
    }
        
    public static bool IsHiddenOverride (MethodDefinition methodDef) {
      if (methodDef.IsConstructor)
        return false;
      if (MethodDeclaredInBase(methodDef, true)) {
        var appearsInItf = methodDef.DeclaringType.Interfaces.Any((itf) => {
          return MethodDeclaredInInterface(itf.Resolve(), methodDef);
        });
        return !appearsInItf;
      }
      return false;
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

    public static MethodDefinition GetMethodDefInBase (MethodDefinition methodDef) { 
      var type = methodDef.DeclaringType;
      var curBase = type.BaseType;
      var methodName = GetUnadornedMethodName(methodDef);
      while ( curBase != null ) {
        var resolved = curBase.Resolve();
        foreach ( var baseMethod in resolved.Methods ) {
          if ( GetUnadornedMethodName(baseMethod) == methodName ) {
            return baseMethod;
          }
        }
        curBase = resolved.BaseType;
      }
      return null;
    }

    public static bool MethodDeclaredInBase(MethodDefinition methodDef, bool exactArgs=true, bool exactAccess=true) {
      var type = methodDef.DeclaringType;
      var curBase = type.BaseType;
      var methodName = GetUnadornedMethodName(methodDef);
      while ( curBase != null ) {
        var resolved = curBase.Resolve();
        foreach ( var baseMethod in resolved.Methods ) {
          if ( (!exactArgs || baseMethod.Parameters.Equals(methodDef.Parameters)) && 
              GetUnadornedMethodName(baseMethod) == methodName &&
              (!exactAccess || baseMethod.IsPublic == methodDef.IsPublic) ) {
            var appearsInItf = baseMethod.DeclaringType.Interfaces.Any((itf) => {
              return MethodDeclaredInInterface(itf.Resolve(), baseMethod);
            });
            return baseMethod.IsPublic || appearsInItf;
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

    public static string MakeMethodAttributes(MethodDefinition methodDef, bool forceOverride=false) {
      var sb = new StringBuilder();
      if ( methodDef.IsPublic && !methodDef.DeclaringType.IsInterface ) {
        sb.Append("public ");
      }
      if ( methodDef.IsStatic ) {
        sb.Append("static ");
      }
      if ( forceOverride || 
          ( !methodDef.IsConstructor && !methodDef.IsStatic && !methodDef.IsGetter && !methodDef.IsSetter && 
             methodDef.DeclaringType.BaseType != null && MethodDeclaredInBase(methodDef, false) ) ) {
        sb.Append("override ");
      }
      return sb.ToString();
    }

    public static TypeReference GetTopLevelDeclaringType (TypeReference typeRef) {
      var declaring = typeRef.DeclaringType;
      while (declaring != null && declaring.DeclaringType != null) {
        declaring = declaring.DeclaringType;
      }
      return declaring;
    }

    public static string MakeTypeName (TypeReference typeRef, bool useExactType = false) {
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
          return "dotnet.system.Array";
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
        case "System.Single":
          return "Single";
        case "System.Double":
          return "Float";
        case "System.Int32":
          return "Int";
        case "System.UInt32":
          return "UInt";
        case "System.Type":
          return "cs.system.Type";

        case "UIDrawCall/Clipping":
          return "UIDrawCall.UIDrawCall_Clipping";
        case "UIAtlas/Sprite":
          return "UIAtlas.UIAtlas_Sprite";
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
          return "Single";
        case "System.Double":
          return "Float";
        case "System.SByte":
        case "System.Int16":
        case "System.Int32":
          return "Int";
        case "System.UInt16":
        case "System.UInt32":
          return "UInt";
        case "System.Type":
          return "cs.system.Type";

        case "UIDrawCall/Clipping":
          return "UIDrawCall.UIDrawCall_Clipping";
        case "UIAtlas/Sprite":
          return "UIAtlas.UIAtlas_Sprite";
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
      string ns = null;
      if (typeRef.Namespace != null && typeRef.Namespace.Length > 0 && typeRef.Namespace != curNs) {
        ns = typeRef.Namespace;
      } else if (typeRef.DeclaringType != null) { 
        var declaringType = GetTopLevelDeclaringType(typeRef);
        if ( declaringType != curType ) {
          if (declaringType.Namespace != null && declaringType.Namespace.Length > 0) {
            ns = declaringType.Namespace;
          } else {
            ns = curNs;
          }
        }
      }

      if (ns != null) {
        string overriden;
        if (overriddenNamespaces.TryGetValue(ns, out overriden)) {
          sb.Append(overriden + ".");
        } else if (rootPackages.Contains(ns.ToLower())) {
          sb.Append(ns.ToLower() + ".");
        } else {
          sb.Append("dotnet." + ns.ToLower() + ".");
        }
      }

      if (typeRef.DeclaringType != null) {
        var declaringType = GetTopLevelDeclaringType(typeRef);
        if ( declaringType != curType ) {
          sb.Append(GetFinalTypeBaseName(declaringType));
          sb.Append(".");
        }
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
                  return "dotnet.system.collections.IEnumerable"; // Anything that implements IEnumerable<T> always implements IEnumerable.
                } else if ( typeRef.Namespace == "System.Collections.Generic" && typeBaseName == "IComparer" ) {
                  return "dotnet.system.collections.IComparer"; // Anything that implements IEnumerable<T> always implements IEnumerable.
                } else if ( typeRef.Namespace == "System" && typeBaseName == "IComparable" ) {
                  return "dotnet.system.IComparable"; // Anything that implements IComparable<T> always implements IComparable.
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
        var hxTypeName = MakeTypeName(paramDef.ParameterType);
        if ( paramDef.IsOut ) {
          hxTypeName = "cs.Out<" + hxTypeName + ">";
        } else if ( paramDef.ParameterType.IsByReference ) {
          hxTypeName = "cs.Ref<" + hxTypeName + ">";
        }
        sb.Append(hxTypeName);
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

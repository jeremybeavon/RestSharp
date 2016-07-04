using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using System.Text.RegularExpressions;
using Ax.DataAccess;
using Ax.Frameworks.BOF;
using Ax.Frameworks.BOF.Attributes;
using Ax.Frameworks.BOF.Data;
using Ax.Frameworks.BOF.DbFieldInfo;
using Ax.Frameworks.SysUtils;
using Ax.Frameworks.ValueObjects;

namespace Ax.UnitTests.IntegrationTestRunners
{
	internal sealed class IntegrationTestBuilder : IServiceInstanceProvider
	{
		private const string unitTestNamespacePrefix = "Ax.UnitTests.IntegrationTestRunners.";
		private const string integrationTestBaseNamespace = "Ax.IntegrationTests";
		private static readonly Assembly dataAccessAssembly = typeof(DAContract).Assembly;
		private static readonly string integrationTestRunnersFolder = Path.Combine(
			Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"..\..")),
			@"IntegrationTestRunners");

		private static readonly Regex typeRegex = new Regex(@"^(?<Namespace>.*)\.(?<Type>[^\.]+)$", RegexOptions.Compiled);
		private static readonly ISet<Type> typesToUnwrapObjectParameters = new HashSet<Type>
		{
			typeof(IAdoHelper),
			typeof(IDataParameterCollection)
		};

		private static readonly ISet<Type> dataAccessTypes = new HashSet<Type>
		{
			typeof(IBOFDbBase),
			typeof(IContextInternal)
		};

		private static readonly ISet<Type> nonPassThroughTypes = new HashSet<Type>
		{
			typeof(IAdoHelper)
		};

		private static readonly IDictionary<TypeCode, string> predefinedTypeNames = new Dictionary<TypeCode, string>
		{
			{TypeCode.Boolean, "bool"},
			{TypeCode.Char, "char"},
			{TypeCode.SByte, "sbyte"},
			{TypeCode.Byte, "byte"},
			{TypeCode.Int16, "short"},
			{TypeCode.UInt16, "ushort"},
			{TypeCode.Int32, "int"},
			{TypeCode.UInt32, "uint"},
			{TypeCode.Int64, "long"},
			{TypeCode.UInt64, "ulong"},
			{TypeCode.Single, "float"},
			{TypeCode.Double, "double"},
			{TypeCode.Decimal, "decimal"},
			{TypeCode.String, "string"}
		};

		private readonly IServiceInstanceProvider instanceProvider;
		private readonly IDictionary<Type, object> resolvedInstances;
		private readonly IDictionary<object, object> returnedInstances;
		private readonly IDictionary<object, object> parameterInstances;
		private readonly HashSet<Type> passThroughTypes;
		private readonly Stack<MemberCallNode> memberCallStack;
		private readonly IntegrationTestRunner testRunner;
		private bool isTestRunning;

		public IntegrationTestBuilder(IntegrationTestRunner testRunner)
		{
			instanceProvider = ServiceFactory.ServiceInstanceProvider;
			resolvedInstances = new Dictionary<Type, object>();
			returnedInstances = new Dictionary<object, object>();
			parameterInstances = new Dictionary<object, object>(new ProxyEqualityComparer());
			passThroughTypes = new HashSet<Type>();
			memberCallStack = new Stack<MemberCallNode>();
			memberCallStack.Push(new MemberCallNode());
			this.testRunner = testRunner;
		}

		public static string MocksWithoutDataAccessSourceDirectory
		{
			get { return Path.Combine(integrationTestRunnersFolder, "WithoutDataAccess"); }
		}

		public static string MocksWithDataAccessSourceDirectory
		{
			get { return Path.Combine(integrationTestRunnersFolder, "WithDataAccess"); }
		}

		public static string GetTypeName(string testClassName, string testMethodName, bool includeDataAccess)
		{
			Match match = typeRegex.Match(testClassName);
			return string.Concat(
				TypeNameHelper.GetNamespaceName(match.Groups["Namespace"].Value, includeDataAccess),
				".",
				TypeNameHelper.GetClassName(match.Groups["Type"].Value, testMethodName));
		}

		public static string GetFileName(string fullTypeName)
		{
			return Path.Combine(
				integrationTestRunnersFolder,
				fullTypeName.Substring(0, unitTestNamespacePrefix.Length).Replace('.', '\\') + ".cs");
		}

		public void RunIntegrationTest(string testName)
		{
			Reset();
			ServiceFactory.ServiceInstanceProvider = this;
			try
			{
				isTestRunning = true;
				testRunner.RunLocalIntegrationTest(testName);
				isTestRunning = false;
			}
			finally
			{
				ServiceFactory.ServiceInstanceProvider = instanceProvider;
			}

			MethodInfo integrationMethod = testRunner.GetTestMethod(testName);
			GenerateTest(false, integrationMethod);
			GenerateTest(true, integrationMethod);
		}

		public T Resolve<T>(Func<T> constructor)
			where T : class
		{
			object resolvedInstance;
			Type type = typeof(T);
			if (resolvedInstances.TryGetValue(type, out resolvedInstance))
			{
				return (T)resolvedInstance;
			}

			T instance;
			if (type == typeof(IContext))
			{
				instance = (T)Resolve(() => (IContextInternal)constructor());
				resolvedInstances.Add(type, instance);
				return instance;
			}

			instance = (T)new MemberCallBuilder(type, instanceProvider.Resolve(constructor), this).GetTransparentProxy();
			if (!nonPassThroughTypes.Contains(type))
			{
				passThroughTypes.Add(type);
			}

			resolvedInstances.Add(type, instance);
			return instance;
		}

		private static bool IsConstant(object value, Type type)
		{
			if (value == null || type == typeof(Type) || type.IsByRef || type.IsEnum)
			{
				return true;
			}

			if (type == typeof(object))
			{
				type = value.GetType();
			}

			TypeCode typeCode = Type.GetTypeCode(type);
			return typeCode != TypeCode.Object &&
				   typeCode != TypeCode.DBNull;
		}

		private static string GetConstantValue(object value, Type type)
		{
			if (value == null)
			{
				return "null";
			}

			/*string variableName;
			if (returnedObjects.TryGetValue(value, out variableName))
			{
				return variableName;
			}*/

			if (type == typeof(Type))
			{
				return string.Format("typeof({0})", GetTypeName((Type)value));
			}

			if (!type.IsEnum)
			{
				return GetConstantValueFromTypeCode(value);
			}

			string typeName = GetTypeName(type);
			return string.Join(
				" | ",
				value.ToString().Split(',').Select(enumValue => string.Format("{0}.{1}", typeName, enumValue.Trim())));
		}

		private static string GetConstantValueFromTypeCode(object value)
		{
			switch (Type.GetTypeCode(value.GetType()))
			{
				case TypeCode.Char:
					return string.Format("'{0}'", value);
				case TypeCode.String:
					return string.Format("@\"{0}\"", value.ToString().Replace("\"", "\"\""));
				case TypeCode.DateTime:
					return GetDateString((DateTime)value);
				case TypeCode.Decimal:
					return string.Concat(value, "m");
				default:
					return value.ToString().ToLower();
			}
		}

		private static string GetDateString(DateTime date)
		{
			return string.Format(
				"new DateTime({0}, {1}, {2}, {3}, {4}, {5}, {6})",
				date.Year,
				date.Month,
				date.Day,
				date.Hour,
				date.Minute,
				date.Second,
				date.Millisecond);
		}

		private static string GetUnformattedVariableName(Type type)
		{
			if (type.IsArray)
			{
				return GetUnformattedVariableName(type.GetElementType()) + "s";
			}

			string typeName = type.Name;
			if (type.IsInterface)
			{
				typeName = typeName.Substring(1);
			}

			if (type.IsGenericType)
			{
				typeName = typeName.Substring(0, typeName.Length - 2);
			}

			return typeName;
		}

		private static string GetTypeName(Type type)
		{
			if (type.IsByRef)
			{
				type = type.GetElementType();
			}

			string predefinedTypeName;
			if (!type.IsEnum && predefinedTypeNames.TryGetValue(Type.GetTypeCode(type), out predefinedTypeName))
			{
				return predefinedTypeName;
			}

			if (type == typeof(void))
			{
				return "void";
			}

			var typeName = new StringBuilder();
			BuildTypeName(type, typeName);
			return typeName.ToString();
		}

		private static void BuildTypeName(Type type, StringBuilder typeName)
		{
			if (type.DeclaringType != null && !type.IsGenericParameter)
			{
				typeName.Append(GetTypeName(type.DeclaringType)).Append(".");
			}

			if (type.IsArray)
			{
				typeName.Append(string.Concat(GetTypeName(type.GetElementType()), "[", string.Empty.PadLeft(type.GetArrayRank() - 1, ','), "]"));
			}
			else if (type.IsGenericType)
			{
				BuildGenericTypeName(type, typeName);
			}
			else
			{
				typeName.Append(type.Name);
			}
		}

		private static void BuildGenericTypeName(Type type, StringBuilder typeName)
		{
			typeName.Append(type.Name.Substring(0, type.Name.Length - 2)).Append("<");
			string comma = string.Empty;
			foreach (Type genericParameter in type.GetGenericArguments())
			{
				typeName.Append(comma).Append(GetTypeName(genericParameter));
				comma = ", ";
			}

			typeName.Append(">");
		}

		private static MethodInfo GetMethod<T>(Expression<Action<T>> expression)
		{
			return ((MethodCallExpression)expression.Body).Method;
		}

		private void Reset()
		{
			resolvedInstances.Clear();
			returnedInstances.Clear();
			parameterInstances.Clear();
			passThroughTypes.Clear();
			memberCallStack.Clear();
			memberCallStack.Push(new MemberCallNode());
		}

		private void AddReturnValue(MemberCall memberCall)
		{
			memberCallStack.Peek().MemberCall = memberCall;
		}

		private void GenerateTest(bool includeDataAccess, MethodInfo integrationTestMethod)
		{
			new CodeGenerator(this, includeDataAccess, integrationTestMethod).GenerateTest();
		}

		private static class TypeNameHelper
		{
			public static string GetFileName(
				string namespaceName,
				bool includeDataAccess,
				string typeName,
				string methodName)
			{
				return Path.Combine(
					integrationTestRunnersFolder,
					includeDataAccess ? "WithDataAccess" : "WithoutDataAccess",
					GetNamespaceSuffix(namespaceName).Replace('.', '\\'),
					GetClassName(typeName, methodName) + ".cs");
			}

			public static string GetNamespaceName(string namespaceName, bool includeDataAccess)
			{
				return string.Concat(
					unitTestNamespacePrefix,
					includeDataAccess ? "WithDataAccess." : "WithoutDataAccess.",
					GetNamespaceSuffix(namespaceName));
			}

			public static string GetClassName(string typeName, string methodName)
			{
				int methodNameHashCode = methodName.GetHashCode();
				return string.Format("{0}_{1}{2}", typeName, methodNameHashCode < 0 ? "N" : "P", Math.Abs(methodNameHashCode));
			}

			private static string GetNamespaceSuffix(string namespaceName)
			{
				if (namespaceName != integrationTestBaseNamespace)
				{
					if (namespaceName == null || !namespaceName.StartsWith(integrationTestBaseNamespace))
					{
						throw new NotSupportedException("Invalid integration namespace name: " + namespaceName);
					}

					return namespaceName.Substring(integrationTestBaseNamespace.Length + 1);
				}

				return string.Empty;
			}
		}

		private sealed class MemberCall
		{
			public MemberCall(MemberInfo member, Type declaringType, object[] parameters, object returnValue)
			{
				Member = member;
				DeclaringType = declaringType;
				Parameters = parameters;
				ReturnValue = returnValue;
			}

			public MemberInfo Member { get; private set; }

			public Type DeclaringType { get; private set; }

			public object[] Parameters { get; private set; }

			public object ReturnValue { get; private set; }

			public Type ReturnType
			{
				get
				{
					var method = Member as MethodInfo;
					return method == null ? ((PropertyInfo)Member).PropertyType : method.ReturnType;
				}
			}
		}

		private sealed class MemberCallNode
		{
			public MemberCallNode()
			{
				ChildNodes = new Dictionary<MemberInfo, MemberCallNodes>();
			}

			public MemberCall MemberCall { get; set; }

			public IDictionary<MemberInfo, MemberCallNodes> ChildNodes { get; private set; }
		}

		private sealed class MemberCallNodes : List<MemberCallNode>
		{
		}

		private sealed class ProxyEqualityComparer : IEqualityComparer<object>
		{
			bool IEqualityComparer<object>.Equals(object x, object y)
			{
				var proxy1 = x as IProxy;
				var proxy2 = y as IProxy;
				return (proxy1 == null && proxy2 == null && Equals(x, y)) || 
					(proxy1 != null && proxy2 != null && proxy1.RealObject == proxy2.RealObject);
			}

			public int GetHashCode(object obj)
			{
				return obj.GetHashCode();
			}
		}

		private interface IProxy
		{
			object RealObject { get; }
		}

		private sealed class MemberCallBuilder : RealProxy, IRemotingTypeInfo, IProxy
		{
			private readonly Type instanceType;
			private readonly object instance;
			private readonly IntegrationTestBuilder builder;
			private readonly bool unwrapObjectParameter;
			private readonly Type[] interfaceTypes;

			public MemberCallBuilder(Type instanceType, object instance, IntegrationTestBuilder builder)
				: base(typeof(ContextBoundObject))
			{
				this.instanceType = instanceType;
				this.instance = instance;
				this.builder = builder;
				unwrapObjectParameter = typesToUnwrapObjectParameters.Contains(instanceType);
				interfaceTypes = (new[] {instanceType}).Concat(instanceType.GetInterfaces()).ToArray();
			}

			public string TypeName
			{
				get { return GetType().Name; }
				set { }
			}

			public object RealObject
			{
				get { return this; }
			}

			public bool CanCastTo(Type fromType, object o)
			{
				return fromType == instanceType || fromType == typeof(IProxy);
			}

			public override IMessage Invoke(IMessage msg)
			{
				var message = msg as IMethodCallMessage;
				if (message == null)
				{
					throw new NotSupportedException();
				}

				if (message.MethodBase.DeclaringType == typeof(IProxy))
				{
					return CreateReturnMessage(message, RealObject, new object[0]);
				}

				if (message.MethodBase.DeclaringType == typeof(object))
				{
					return CreateReturnMessage(message, message.MethodBase.Invoke(this, message.Args), new object[0]);
				}

				if (builder.isTestRunning)
				{
					return Invoke(message);
				}

				object[] parameters = FindParameters(message);
				return CreateReturnMessage(message, message.MethodBase.Invoke(instance, parameters), parameters);
			}

			private static IMessage CreateReturnMessage(IMethodCallMessage message, object returnValue, object[] parameters)
			{
				return new ReturnMessage(returnValue, parameters, parameters.Length, message.LogicalCallContext, message);
			}

			private IMessage Invoke(IMethodCallMessage message)
			{
				MemberInfo member = FindMember(message.MethodBase);
				object[] parameters = FindParameters(message);
				SetUp(member);
				object returnValue;
				try
				{
					returnValue = InvokeMethod(message.MethodBase, parameters);
					HandleReturnValue(member, message.Args, returnValue);
				}
				catch (Exception exception)
				{
					HandleException(member, message.Args, exception);
					throw;
				}
				finally
				{
					CleanUp();
				}

				return CreateReturnMessage(message, returnValue, parameters);
			}

			private MemberInfo FindMember(MethodBase method)
			{
				if (!method.Attributes.HasFlag(MethodAttributes.SpecialName))
				{
					return method;
				}

				ParameterInfo[] parameters = method.GetParameters();
				IEnumerable<Type> parameterTypes = parameters.Select(parameter => parameter.ParameterType);
				if (method.Name.StartsWith("set_"))
				{
					parameterTypes = parameterTypes.Take(parameters.Length - 1);
				}
				else if (!method.Name.StartsWith("get_"))
				{
					return method;
				}

				return FindProperty(method.Name.Substring(4), parameterTypes.ToArray());
			}

			private MemberInfo FindProperty(string propertyName, Type[] parameterTypes)
			{
				return interfaceTypes
					.Select(type => type.GetProperty(propertyName, parameterTypes))
					.First(property => property != null);
			}

			private object[] FindParameters(IMethodMessage message)
			{
				ParameterInfo[] methodParameters = message.MethodBase.GetParameters();
				return message.Args
					.Select((parameter, index) => UnwrapParameterValue(methodParameters[index].ParameterType, parameter))
					.ToArray();
			}

			private object InvokeMethod(MethodBase method, object[] parameters)
			{
				return WrapReturnValue(((MethodInfo)method).ReturnType, method.Invoke(instance, parameters));
			}

			private void SetUp(MemberInfo member)
			{
				MemberCallNode currentNode = builder.memberCallStack.Peek();
				MemberCallNodes childNodes;
				if (!currentNode.ChildNodes.TryGetValue(member, out childNodes))
				{
					childNodes = new MemberCallNodes();
					currentNode.ChildNodes.Add(member, childNodes);
				}

				var newNode = new MemberCallNode();
				childNodes.Add(newNode);
				builder.memberCallStack.Push(newNode);
			}

			private void HandleException(MemberInfo member, object[] parameters, Exception exception)
			{
				builder.AddReturnValue(new MemberCall(member, instanceType, parameters, exception));
			}

			private void HandleReturnValue(MemberInfo member, object[] parameters, object value)
			{
				builder.AddReturnValue(new MemberCall(member, instanceType, parameters, value));
			}

			private void CleanUp()
			{
				builder.memberCallStack.Pop();
			}

			private object WrapReturnValue(Type returnType, object value)
			{
				if (value == null)
				{
					return null;
				}

				if (returnType.IsInterface && returnType != typeof(IBOFDbHelper))
				{
					return WrapInterfaceReturnValue(returnType, value);
				}

				if (!returnType.IsArray || !returnType.GetElementType().IsInterface)
				{
					return value;
				}

				Type returnElementType = returnType.GetElementType();
				var list = (IList)value;
				for (int index = 0; index < list.Count; index++)
				{
					list[index] = WrapInterfaceReturnValue(returnElementType, list[index]);
				}

				return value;
			}

			private object WrapInterfaceReturnValue(Type returnType, object value)
			{
				object wrappedValue;
				if (builder.returnedInstances.TryGetValue(value, out wrappedValue))
				{
					return wrappedValue;
				}

				wrappedValue = new MemberCallBuilder(returnType, value, builder).GetTransparentProxy();
				builder.returnedInstances.Add(value, wrappedValue);
				builder.parameterInstances.Add(wrappedValue, value);
				return wrappedValue;
			}

			private object UnwrapParameterValue(Type parameterType, object value)
			{
				if (value == null)
				{
					return null;
				}

				if (parameterType.IsInterface || (unwrapObjectParameter && parameterType == typeof(object)))
				{
					return UnwrapInterfaceParameterValue(value);
				}

				if ((parameterType.IsArray && parameterType.GetElementType().IsInterface) ||
					(unwrapObjectParameter && parameterType == typeof(object[])))
				{
					UnwrapArrayParameterValue(value);
				}

				return value;
			}

			private void UnwrapArrayParameterValue(object value)
			{
				var list = (IList)value;
				for (int index = 0; index < list.Count; index++)
				{
					list[index] = UnwrapInterfaceParameterValue(list[index]);
				}
			}

			private object UnwrapInterfaceParameterValue(object value)
			{
				object wrappedValue;
				return builder.parameterInstances.TryGetValue(value, out wrappedValue) ? wrappedValue : value;
			}
		}

		private sealed class ReturnValues : List<object>
		{
			public bool IsPassThroughCall { get; set; }
		}

		private sealed class CodeGenerator
		{
			private readonly IntegrationTestBuilder builder;
			private readonly bool includeDataAccess;
			private readonly Type integrationTestType;
			private readonly MethodInfo integrationTestMethod;
			private readonly SortedSet<string> systemReferencedNamespaces;
			private readonly SortedSet<string> referencedNamespaces;
			private readonly IDictionary<string, Type> variableDeclarations;
			private readonly IDictionary<object, string> returnedObjects;
			private readonly IDictionary<string, int> variableNameUsage;
			private readonly IDictionary<string, object> variableValues;
			private readonly IDictionary<string, string> variableSerializedValues;
			private readonly IDictionary<Type, MockedType> typesToMock;

			public CodeGenerator(IntegrationTestBuilder builder, bool includeDataAccess, MethodInfo integrationTestMethod)
			{
				this.builder = builder;
				this.includeDataAccess = includeDataAccess;
				integrationTestType = integrationTestMethod.DeclaringType;
				this.integrationTestMethod = integrationTestMethod;
				systemReferencedNamespaces = new SortedSet<string>
				{
					"System.CodeDom.Compiler"
				};
				referencedNamespaces = new SortedSet<string>(builder.passThroughTypes.Select(type => type.Namespace))
				{
					"Ax.TestingCommon.Extensions",
					"Ax.TestingCommon.UnitTests"
				};
				typesToMock = new Dictionary<Type, MockedType>();
				variableDeclarations = new Dictionary<string, Type>();
				returnedObjects = new Dictionary<object, string>(new ProxyEqualityComparer());
				variableNameUsage = new Dictionary<string, int>();
				variableValues = new Dictionary<string, object>();
				variableSerializedValues = new Dictionary<string, string>();
				if (integrationTestType != null)
				{
					ClassName = TypeNameHelper.GetClassName(integrationTestType.Name, integrationTestMethod.Name);
				}

				BuildExpectations();
			}

			public string ClassName { get; private set; }

			private string FileName
			{
				get
				{
					return TypeNameHelper.GetFileName(
						integrationTestType.Namespace,
						includeDataAccess,
						integrationTestType.Name,
						integrationTestMethod.Name);
				}
			}

			private string NamespaceName
			{
				get { return TypeNameHelper.GetNamespaceName(integrationTestType.Namespace, includeDataAccess); }
			}

			public void GenerateTest()
			{
				var testText = new StringBuilder();
				WriteFileHeader(testText);
				WriteUsingStatements(testText);
				WriteNamespace(testText);
				string fileName = FileName;
				string directory = Path.GetDirectoryName(fileName);
				if (directory != null)
				{
					Directory.CreateDirectory(directory);
				}

				File.WriteAllText(fileName, testText.ToString());
			}

			public ReturnValues BuildReturnValues(MemberCall call)
			{
				return new ReturnValues
				{
					IsPassThroughCall = IsPassThroughCall(call.DeclaringType)
				};
			}

			public void AddReturnValue(MemberCall call, ReturnValues returnValues)
			{
				if (returnValues.IsPassThroughCall || call.ReturnType == typeof(void))
				{
					return;
				}

				object returnValue = call.ReturnValue;
				returnValues.Add(returnValue);
				Type returnType = call.ReturnType;
				if (IsConstant(returnValue, returnType) || returnedObjects.ContainsKey(returnValue))
				{
					return;
				}

				if (returnType.IsInterface && returnType != typeof(IBOFDbHelper))
				{
					returnedObjects.Add(returnValue, GetMockVariableName(returnType));
					return;
				}

				if (returnType.IsArray && returnType.GetElementType().IsInterface)
				{
					BuildInterfaceArrayReturnValue(returnType.GetElementType(), (IEnumerable)returnValue);
				}

				BuildSerializedReturnValue(returnValue, returnType);
			}

			public bool IsPassThroughCall(Type type)
			{
				return builder.passThroughTypes.Contains(type) &&
				       (includeDataAccess || (type.Assembly != dataAccessAssembly && !dataAccessTypes.Contains(type)));
			}

			public string GetVariableName(object returnValue)
			{
				return returnedObjects[returnValue];
			}

			public void AddNamespaces(PropertyInfo property)
			{
				AddNamespaces(property.GetIndexParameters(), property.PropertyType);
			}

			public void AddNamespaces(MethodInfo method)
			{
				AddNamespaces(method.GetParameters(), method.ReturnType);
			}

			private static StringBuilder AppendIndent(StringBuilder textBuilder, int indent)
			{
				return textBuilder.Append(string.Empty.PadLeft(indent, '\t'));
			}

			private static IEnumerable<BOFDbPropInfo> GetSerializableProperties(Type type)
			{
				return BOFDbFldInfoFactory.GetDbFldInfos(type).PropsPublic.Where(IsSerializableProperty);
			}

			private static bool IsSerializableProperty(BOFDbPropInfo property)
			{
				return property.PropInfo.CanWrite &&
				       property.IsXmlSerialized &&
				       !Attribute.IsDefined(property.PropInfo, typeof(BOFSystemPathAttribute));
			}

			private static string GetDummyDataInitializationMethodName(string variableName)
			{
				return "Create" + variableName.Substring(0, 1).ToUpper() + variableName.Substring(1);
			}

			private static string GetMockVariableName(Type type)
			{
				if (type == typeof(IContext))
				{
					type = typeof(IContextInternal);
				}

				return "mock" + GetUnformattedVariableName(type);
			}

			private string GetDummyVariableName(object value, Type type)
			{
				string variableName = null;
				if (value != null)
				{
					type = value.GetType();
					var collection = value as IVOBaseCollection;
					if (collection != null && collection.Count != 0)
					{
						variableName = GetUnformattedVariableName(collection.GetItem(0).GetType()) + "Collection";
					}
				}

				if (variableName == null)
				{
					variableName = GetUnformattedVariableName(type);
				}

				return GetDummyVariableName("dummy" + variableName);
			}

			private string GetDummyVariableName(string variableName)
			{
				if (variableNameUsage.ContainsKey(variableName))
				{
					variableNameUsage[variableName]++;
					return variableName + variableNameUsage[variableName];
				}

				variableNameUsage.Add(variableName, 1);
				return variableName;
			}

			private static void WriteFileHeader(StringBuilder testText)
			{
				testText.AppendLine("/*");
				testText.AppendLine(" * This file is automatically generated. Do not modify.");
				testText.AppendLine("*/");
			}

			private void WriteUsingStatements(StringBuilder testText)
			{
				testText.AppendLine();
				foreach (string referencedNamespace in systemReferencedNamespaces)
				{
					testText.Append("using ").Append(referencedNamespace).AppendLine(";");
				}

				foreach (string referencedNamespace in referencedNamespaces)
				{
					testText.Append("using ").Append(referencedNamespace).AppendLine(";");
				}
			}

			private void WriteNamespace(StringBuilder testText)
			{
				testText.AppendLine();
				testText.AppendFormat("namespace {0}", NamespaceName).AppendLine();
				testText.AppendLine("{");
				WriteClass(testText);
				testText.AppendLine("}");
			}

			private void WriteClass(StringBuilder testText)
			{
				testText.AppendLine("\t/// <summary>");
				testText.AppendFormat(
					"\t/// This class provides mocks for {0}.{1}",
					integrationTestType.FullName,
					integrationTestMethod.Name).AppendLine();
				testText.AppendLine("\t/// </summary>");
				testText.AppendFormat("\t[GeneratedCode(\"{0}\", \"1.0.0.0\")]", typeof(IntegrationTestBuilder).Name).AppendLine();
				testText.AppendFormat("\tpublic sealed class {0} : IntegrationMockTest", ClassName).AppendLine();
				testText.AppendLine("\t{");
				WriteMockFields(testText);
				WriteDummyDataFields(testText);
				WriteSetUpMethod(testText);
				WriteCreateServiceInstanceProviderMethod(testText);
				WriteDummyDataInitializationMethods(testText);
				WriteMockClasses(testText);
				testText.AppendLine("\t}");
			}

			private void WriteMockFields(StringBuilder testText)
			{
				foreach (Type typeToMock in typesToMock.Where(type => !type.Value.IsPassThroughCall).Select(type => type.Key))
				{
					testText.AppendFormat(
						"\t\tprivate {0} {1};",
						typeToMock.Name,
						GetMockVariableName(typeToMock)).AppendLine();
				}
			}

			private void WriteDummyDataFields(StringBuilder testText)
			{
				foreach (KeyValuePair<string, Type> variableDeclaration in variableDeclarations)
				{
					string typeName = GetTypeName(variableDeclaration.Value);
					testText.AppendFormat("\t\tprivate {0} {1}", typeName, variableDeclaration.Key);
					testText.AppendLine(";");
				}
			}

			private void WriteSetUpMethod(StringBuilder testText)
			{
				testText.AppendLine();
				testText.AppendLine("\t\tpublic override void SetUp()");
				testText.AppendLine("\t\t{");
				testText.AppendLine("\t\t\tbase.SetUp();");
				WriteMockVariables(testText);
				WriteDummyDataInitialization(testText);
				testText.AppendLine("\t\t}");
			}

			private void WriteMockVariables(StringBuilder testText)
			{
				foreach (Type typeToMock in typesToMock.Where(type => !type.Value.IsPassThroughCall).Select(type => type.Key))
				{
					string mockVariableName = GetMockVariableName(typeToMock);
					testText.AppendFormat(
						"\t\t\t{0} = new Mock{1}(this);",
						mockVariableName,
						GetUnformattedVariableName(typeToMock)).AppendLine();
					testText.AppendFormat("\t\t\tRegister({0});", mockVariableName).AppendLine();
				}
			}

			private void WriteDummyDataInitialization(StringBuilder testText)
			{
				foreach (KeyValuePair<string, object> variableValue in variableValues)
				{
					if (variableSerializedValues.ContainsKey(variableValue.Key))
					{
						testText.AppendFormat(
							"\t\t\t{0} = {1}();",
							variableValue.Key,
							GetDummyDataInitializationMethodName(variableValue.Key)).AppendLine();
					}
				}
			}

			private void WriteCreateServiceInstanceProviderMethod(StringBuilder testText)
			{
				testText.AppendLine();
				testText.AppendLine("\t\tprotected override IUnitTestServiceInstanceProvider CreateServiceInstanceProvider()");
				testText.AppendLine("\t\t{");
				testText.Append("\t\t\treturn new IntegrationTestServiceInstanceProvider(");
				string comma = string.Empty;
				foreach (Type passThroughType in builder.passThroughTypes
					.Intersect(typesToMock.Values.Where(type => type.IsPassThroughCall).Select(type => type.Type).Distinct())
					.OrderBy(type => type.Name))
				{
					testText.AppendLine(comma).AppendFormat("\t\t\t\ttypeof({0})", GetTypeName(passThroughType));
					comma = ",";
				}

				testText.AppendLine(");");
				testText.AppendLine("\t\t}");
			}

			private void WriteDummyDataInitializationMethods(StringBuilder testText)
			{
				foreach (KeyValuePair<string, string> variableValue in variableSerializedValues)
				{
					testText.AppendLine();
					testText.AppendFormat(
						"\t\tprivate {0} {1}()",
						GetTypeName(variableDeclarations[variableValue.Key]),
						GetDummyDataInitializationMethodName(variableValue.Key)).AppendLine();
					testText.AppendLine("\t\t{");
					testText.AppendLine(variableValue.Value);
					testText.AppendLine("\t\t}");
				}
			}

			private void WriteMockClasses(StringBuilder testText)
			{
				foreach (MockedType typeToMock in typesToMock.Values.Where(type => !type.IsPassThroughCall))
				{
					new MockedTypeCodeGenerator(typeToMock).Generate(testText);
				}
			}

			private void BuildExpectations()
			{
				BuildExpectations(builder.memberCallStack.Peek().ChildNodes.Values);
			}

			private void BuildExpectations(IEnumerable<MemberCallNodes> nodes)
			{
				foreach (MemberCallNode node in nodes.SelectMany(value => value))
				{
					MemberCall call = node.MemberCall;
					MockedType mockedType;
					if (!typesToMock.TryGetValue(call.DeclaringType, out mockedType))
					{
						mockedType = new MockedType(call.DeclaringType, this);
						typesToMock.Add(call.DeclaringType, mockedType);
					}

					mockedType.AddMemberCall(call);
					if (mockedType.IsPassThroughCall)
					{
						BuildExpectations(node.ChildNodes.Values);
					}
				}
			}

			private void AddNamespaces(IEnumerable<ParameterInfo> parameters, Type returnType)
			{
				foreach (ParameterInfo parameter in parameters)
				{
					AddNamespace(parameter.ParameterType);
				}

				if (returnType != typeof(void))
				{
					AddNamespace(returnType);
				}
			}

			private void BuildInterfaceArrayReturnValue(Type elementType, IEnumerable returnValue)
			{
				if (!typesToMock.ContainsKey(elementType))
				{
					typesToMock.Add(elementType, new MockedType(elementType, this));
				}

				string mockVariableName = GetMockVariableName(elementType);
				foreach (object item in returnValue)
				{
					returnedObjects.Add(item, mockVariableName);
				}
			}

			private void BuildSerializedReturnValue(object returnValue, Type returnType)
			{
				string variableName = GetDummyVariableName(returnValue, returnType);
				variableDeclarations.Add(variableName, returnType);
				returnedObjects.Add(returnValue, variableName);
				variableValues.Add(variableName, returnValue);
				if (!predefinedTypeNames.ContainsKey(Type.GetTypeCode(returnType)))
				{
					AddNamespace(returnType.Namespace);
				}

				variableSerializedValues.Add(variableName, SerializeReturnValue(returnValue));
			}

			private string SerializeReturnValue(object returnValue)
			{
				var voBase = returnValue as VOBase;
				if (voBase != null)
				{
					return SerializeVoBase(voBase);
				}

				var collection = returnValue as IList;
				if (collection != null)
				{
					return SerializeCollection(collection);
				}

				var dataSet = returnValue as DataSet;
				if (dataSet != null)
				{
					return SerializeDataSet(dataSet);
				}

				if (returnValue is IBOFDbHelper)
				{
					return SerializeBofDbHelper();
				}

				throw new NotSupportedException();
			}

			private string SerializeVoBase(VOBase voBase)
			{
				var textBuilder = new StringBuilder();
				AppendIndent(textBuilder, 3).Append("return ");
				SerializeVoBase(voBase, textBuilder, 3, true);
				textBuilder.Append(";");
				return textBuilder.ToString();
			}

			private void SerializeVoBase(VOBase voBase, StringBuilder textBuilder, int indent, bool checkDefaultValue)
			{
				textBuilder.AppendFormat("new {0}(context)", GetTypeName(voBase.GetType())).AppendLine();
				AppendIndent(textBuilder, indent).Append("{");
				indent++;
				string comma = string.Empty;
				foreach (BOFDbPropInfo property in GetSerializableProperties(voBase.GetType()))
				{
					object value = property.PropGetter(voBase);
					if (value == null || !checkDefaultValue || !voBase.IsDefaultValue(voBase.GetType(), value, property.PropInfo))
					{
						textBuilder.AppendLine(comma);
						comma = ",";
						AppendIndent(textBuilder, indent).AppendFormat("{0} = ", property.PropInfo.Name);
						if (IsConstant(value, (value ?? new object()).GetType()))
						{
							textBuilder.Append(GetConstantValue(value, property.PropInfo.PropertyType));
						}
						else
						{
							SerializeVoBasePropertyValue(value, textBuilder, indent, checkDefaultValue);
						}
					}
				}

				indent--;
				textBuilder.AppendLine().Append(string.Empty.PadLeft(indent, '\t')).Append("}");
			}

			private void SerializeVoBasePropertyValue(object value, StringBuilder textBuilder, int indent, bool checkDefaultValue)
			{
				var childVoBase = value as VOBase;
				if (childVoBase != null)
				{
					SerializeVoBase(childVoBase, textBuilder, indent, checkDefaultValue);
				}
				else
				{
					var collection = value as IList;
					if (collection == null)
					{
						throw new NotSupportedException();
					}

					SerializeCollection(collection, textBuilder, indent, checkDefaultValue);
				}
			}

			private string SerializeCollection(IList collection)
			{
				var textBuilder = new StringBuilder();
				AppendIndent(textBuilder, 3).Append("return ");
				SerializeCollection(collection, textBuilder, 3, collection.Count != 0 && !(collection[0] is VOSystemDefs));
				textBuilder.Append(";");
				return textBuilder.ToString();
			}

			private void SerializeCollection(ICollection collection, StringBuilder textBuilder, int indent, bool checkDefaultValue)
			{
				if (collection.GetType().IsArray)
				{
					SerializeArray(collection, textBuilder, indent);
					return;
				}

				textBuilder.AppendFormat("new {0}(context)", GetTypeName(collection.GetType()));
				if (collection.Count == 0)
				{
					return;
				}

				textBuilder.AppendLine();
				AppendIndent(textBuilder, indent).Append("{");
				indent++;
				string comma = string.Empty;
				foreach (object item in collection)
				{
					var voBase = item as VOBase;
					if (voBase == null)
					{
						throw new NotSupportedException();
					}

					textBuilder.AppendLine(comma);
					comma = ",";
					AppendIndent(textBuilder, indent);
					SerializeVoBase(voBase, textBuilder, indent, checkDefaultValue);
				}

				indent--;
				textBuilder.AppendLine().Append(string.Empty.PadLeft(indent, '\t')).Append("}");
			}

			private void SerializeArray(ICollection collection, StringBuilder textBuilder, int indent)
			{
				textBuilder.AppendFormat("new {0}[", GetTypeName(collection.GetType().GetElementType()));
				if (collection.Count == 0)
				{
					textBuilder.Append("0]");
					return;
				}

				textBuilder.AppendLine("]");
				AppendIndent(textBuilder, indent).Append("{");
				indent++;
				Type itemType = collection.GetType().GetElementType();
				string comma = string.Empty;
				foreach (object item in collection)
				{
					textBuilder.AppendLine(comma);
					comma = ",";
					AppendIndent(textBuilder, indent);
					string value = IsConstant(item, itemType) ?
						GetConstantValue(item, item.GetType()) :
						returnedObjects[item];
					textBuilder.Append(value);
				}

				indent--;
				textBuilder.AppendLine().Append(string.Empty.PadLeft(indent, '\t')).Append("}");
			}

			private string SerializeDataSet(DataSet dataSet)
			{
				var textBuilder = new StringBuilder();
				AppendIndent(textBuilder, 3).AppendFormat("const string xml = {0};", GetConstantValue(dataSet.GetXml(), typeof(string))).AppendLine();
				AppendIndent(textBuilder, 3).AppendLine("var dataSet = new DataSet();");
				AddNamespace(typeof(StringReader));
				AppendIndent(textBuilder, 3).AppendLine("using (TextReader reader = new StringReader(xml))");
				AppendIndent(textBuilder, 3).AppendLine("{");
				AppendIndent(textBuilder, 4).AppendLine("dataSet.ReadXml(xml);");
				AppendIndent(textBuilder, 3).AppendLine("}");
				textBuilder.AppendLine();
				AppendIndent(textBuilder, 3).Append("return dataSet;");
				return textBuilder.ToString();
			}

			private string SerializeBofDbHelper()
			{
				AddContextExpectationIfNecessary(context => context.ValidateManagedThreadId());
				AddContextExpectationIfNecessary(context => context.IsOracle());
				return "\t\t\treturn new BOFDbHelper(context);";
			}

			private void AddContextExpectationIfNecessary(Expression<Action<IContext>> methodExpression)
			{
				typesToMock[typeof(IContextInternal)].AddMemberCall(
					new MemberCall(GetMethod(methodExpression), typeof(IContextInternal), new object[0], false));
			}

			private void AddNamespace(string namespaceName)
			{
				if (namespaceName.StartsWith("System"))
				{
					systemReferencedNamespaces.Add(namespaceName);
				}
				else
				{
					referencedNamespaces.Add(namespaceName);
				}
			}

			private void AddNamespace(Type type)
			{
				if (type.DeclaringType != null)
				{
					AddNamespace(type.DeclaringType);
				}
				else if (type.IsByRef || type.IsArray)
				{
					AddNamespace(type.GetElementType());
				}
				else
				{
					AddNamespace(type.Namespace);
					if (type.IsGenericType)
					{
						foreach (Type genericParameter in type.GetGenericArguments())
						{
							AddNamespace(genericParameter);
						}
					}
				}
			}
		}

		private sealed class MockedTypeCodeGenerator
		{
			private const GenericParameterAttributes genericParameterFlags =
				GenericParameterAttributes.ReferenceTypeConstraint |
				GenericParameterAttributes.NotNullableValueTypeConstraint |
				GenericParameterAttributes.DefaultConstructorConstraint;

			private readonly MockedType mockedType;
			private readonly StringBuilder fields;
			private readonly StringBuilder constructorText;
			private readonly StringBuilder memberText;
			private readonly IDictionary<string, int> methodCallExpectations;

			public MockedTypeCodeGenerator(MockedType mockedType)
			{
				this.mockedType = mockedType;
				fields = new StringBuilder();
				constructorText = new StringBuilder();
				memberText = new StringBuilder();
				methodCallExpectations = new Dictionary<string, int>();
			}

			public void Generate(StringBuilder testText)
			{
				WriteMockedType();
				string className = "Mock" + GetUnformattedVariableName(mockedType.Type);
				testText.AppendLine();
				testText.AppendFormat(
					"\t\tprivate sealed class {0} : {1}",
					className,
					GetTypeName(mockedType.Type)).AppendLine();
				testText.AppendLine("\t\t{");
				testText.AppendFormat("\t\t\tprivate readonly {0} test;", mockedType.ParentClassName).AppendLine();
				testText.AppendLine(fields.ToString());
				WriteConstructor(testText, className);
				testText.Append(memberText);
				testText.AppendLine("\t\t}");
			}

			private static void AppendOutOrRefIfNecessary(StringBuilder testText, ParameterInfo parameter)
			{
				if (parameter.ParameterType.IsByRef)
				{
					testText.Append(parameter.IsOut ? "out " : "ref ");
				}
			}

			private static bool ContainsOnlyNonConstantParameters(ParameterValues parameters, IList<ParameterInfo> methodParameters)
			{
				return parameters.Values
					.Where((value, index) => !IsConstant(value, methodParameters[index].ParameterType))
					.Count() == parameters.Values.Length;
			}

			private void WriteMockedType()
			{
				foreach (MockedProperty property in mockedType.MockedProperties)
				{
					WriteMockedProperty(property);
				}

				foreach (MockedMethod method in mockedType.MockedMethods)
				{
					WriteMockedMethod(method);
				}
			}

			private void WriteConstructor(StringBuilder testText, string className)
			{
				testText.AppendFormat("\t\t\tpublic {0}({1} test)", className, mockedType.ParentClassName).AppendLine();
				testText.AppendLine("\t\t\t{");
				testText.AppendLine("\t\t\t\tthis.test = test;");
				testText.Append(constructorText);
				testText.AppendLine("\t\t\t}");
			}

			private void WriteMockedProperty(MockedProperty property)
			{
				memberText.AppendLine();
				if (property.Property.GetIndexParameters().Length == 0)
				{
					memberText.AppendFormat(
						"\t\t\tpublic {0} {1}",
						GetTypeName(property.Property.PropertyType),
						property.Property.Name).AppendLine();
					WriteMockedPropertyBody(property, WriteMockedPropertyGetExpectations, WriteMockedPropertySetExpectations);
				}
				else
				{
					WriteMockedIndexer(property);
				}
			}

			private void WriteMockedIndexer(MockedProperty property)
			{
				memberText.AppendFormat(
					"\t\t\tpublic {0} this[",
					GetTypeName(property.Property.PropertyType));
				string comma = string.Empty;
				foreach (ParameterInfo parameter in property.Property.GetIndexParameters())
				{
					memberText.Append(comma).AppendFormat("{0} {1}", GetTypeName(parameter.ParameterType), parameter.Name);
					comma = ", ";
				}

				memberText.AppendLine("]");
				WriteMockedPropertyBody(property, WriteMockedIndexerGetExpectations, WriteMockedIndexerSetExpectations);
			}

			private void WriteMockedIndexerGetExpectations(MockedProperty property)
			{
				WriteThrowUnexpectedCallException(5);
			}

			private void WriteMockedIndexerSetExpectations(MockedProperty property)
			{
				WriteThrowUnexpectedCallException(5);
			}

			private void WriteThrowUnexpectedCallException(int indent)
			{
				memberText.Append(string.Empty.PadLeft(indent, '\t')).AppendLine("throw new UnexpectedCallException();");
			}

			private void WriteMockedPropertyBody(
				MockedProperty property,
				Action<MockedProperty> buildGetExpectationsAction,
				Action<MockedProperty> buildSetExpectationsAction)
			{
				memberText.AppendLine("\t\t\t{");
				if (property.Property.CanRead)
				{
					memberText.AppendLine("\t\t\t\tget");
					memberText.AppendLine("\t\t\t\t{");
					buildGetExpectationsAction(property);
					memberText.AppendLine("\t\t\t\t}");
				}

				if (property.Property.CanWrite)
				{
					memberText.AppendLine("\t\t\t\tset");
					memberText.AppendLine("\t\t\t\t{");
					buildSetExpectationsAction(property);
					memberText.AppendLine("\t\t\t\t}");
				}

				memberText.AppendLine("\t\t\t}");
			}

			private void WriteMockedPropertyGetExpectations(MockedProperty property)
			{
				if (property.ExpectedParameterValues.Keys.All(parameters => parameters.Values.Length != 0))
				{
					WriteThrowUnexpectedCallException(5);
				}
				else
				{
					string expectationFieldName = GetExpectationFieldName(property.Property.Name);
					fields.AppendFormat(
						"\t\t\tprivate readonly MethodCallReturnExpectation {0};",
						expectationFieldName).AppendLine();
					constructorText.AppendFormat("\t\t\t\t{0} = new MethodCallReturnExpectation(", expectationFieldName).AppendLine();
					constructorText.AppendFormat("\t\t\t\t\t\"{0}.{1}\",", mockedType.Type.Name, property.Property.Name).AppendLine();
					constructorText.AppendFormat("\t\t\t\t\t{0}", property.ExpectedParameterValues.Count);
					WriteReturnValues(property.ExpectedParameterValues[new ParameterValues()], property.Property.PropertyType);
					constructorText.AppendLine(");");
					memberText.AppendFormat(
						"\t\t\t\t\treturn {0}.Return<{1}>();",
						expectationFieldName,
						GetTypeName(property.Property.PropertyType)).AppendLine();
				}
			}

			private void WriteMockedPropertySetExpectations(MockedProperty property)
			{
				if (property.ExpectedParameterValues.Keys.All(parameters => parameters.Values.Length == 0))
				{
					WriteThrowUnexpectedCallException(5);
				}
			}

			private void WriteMockedMethod(MockedMethod method)
			{
				memberText.AppendLine();
				memberText.AppendFormat("\t\t\tpublic {0} {1}", GetTypeName(method.Method.ReturnType), method.Method.Name);
				WriteMockedMethodGenericParameters(method);
				memberText.Append("(");
				WriteMockedMethodParameters(method);
				memberText.AppendLine(")");
				WriteMockedMethodGenericConstraints(method);
				memberText.AppendLine("\t\t\t{");
				WriteMockedMethodExpectations(method);
				memberText.AppendLine("\t\t\t}");
			}

			private void WriteMockedMethodGenericParameters(MockedMethod method)
			{
				if (!method.Method.IsGenericMethod)
				{
					return;
				}

				memberText.Append("<");
				memberText.Append(string.Join(", ", method.Method.GetGenericArguments().Select(type => type.Name)));
				memberText.Append(">");
			}

			private void WriteMockedMethodGenericConstraints(MockedMethod method)
			{
				if (!method.Method.IsGenericMethod)
				{
					return;
				}

				foreach (Type genericParameter in method.Method.GetGenericArguments())
				{
					Type[] parameterConstraints = genericParameter.GetGenericParameterConstraints();
					if ((genericParameter.GenericParameterAttributes & genericParameterFlags) != GenericParameterAttributes.None ||
					    parameterConstraints.Length != 0)
					{
						memberText.AppendFormat("\t\t\t\twhere {0} : ", genericParameter.Name);
						WriteMockedMethodGenericConstraints(genericParameter.GenericParameterAttributes, parameterConstraints);
						memberText.AppendLine();
					}
				}
			}

			private void WriteMockedMethodGenericConstraints(GenericParameterAttributes attributes, IEnumerable<Type> parameterConstraints)
			{
				const string commaText = ", ";
				string comma = string.Empty;
				if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
				{
					memberText.Append("class");
					comma = commaText;
				}
				else if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
				{
					memberText.Append("struct");
					comma = commaText;
				}

				foreach (Type parameterConstraint in parameterConstraints)
				{
					memberText.Append(comma).Append(GetTypeName(parameterConstraint));
					comma = commaText;
				}

				if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint))
				{
					memberText.Append(comma).Append("new()");
				}
			}

			private void WriteMockedMethodParameters(MockedMethod method)
			{
				string comma = string.Empty;
				foreach (ParameterInfo parameter in method.Method.GetParameters())
				{
					memberText.AppendLine(comma);
					comma = ",";
					memberText.Append("\t\t\t\t");
					AppendOutOrRefIfNecessary(memberText, parameter);
					if (Attribute.IsDefined(parameter, typeof(ParamArrayAttribute)))
					{
						memberText.Append("params ");
					}

					memberText.AppendFormat("{0} {1}", GetTypeName(parameter.ParameterType), parameter.Name);
				}
			}

			private void WriteMockedMethodOutputParameters(MockedMethod method)
			{
				foreach (ParameterInfo parameter in method.Method.GetParameters().Where(parameter => parameter.IsOut))
				{
					memberText.AppendFormat(
						"\t\t\t\t{0} = default({1});",
						parameter.Name,
						GetTypeName(parameter.ParameterType)).AppendLine();
				}
			}

			private void WriteMockedMethodExpectations(MockedMethod method)
			{
				WriteMockedMethodOutputParameters(method);
				KeyValuePair<ParameterValues, ReturnValues>? defaultCall = null;
				ParameterInfo[] methodParameters = method.Method.GetParameters();
				foreach (KeyValuePair<ParameterValues, ReturnValues> expectedParameterValues in method.ExpectedParameterValues)
				{
					if (expectedParameterValues.Key.GenericParameterTypes.Length == 0 &&
						ContainsOnlyNonConstantParameters(expectedParameterValues.Key, methodParameters))
					{
						defaultCall = expectedParameterValues;
					}
					else
					{
						WriteMockedMethodExpectation(method, expectedParameterValues, methodParameters);
					}
				}

				WriteDefaultMockedMethodExpectation(method, defaultCall, methodParameters);
			}

			private void WriteDefaultMockedMethodExpectation(
				MockedMethod method,
				KeyValuePair<ParameterValues, ReturnValues>? defaultCall,
				IList<ParameterInfo> methodParameters)
			{
				if (defaultCall.HasValue)
				{
					memberText.Append("\t\t\t\t");
					WriteMockedMethodCallExpectation(method, defaultCall.Value, methodParameters);
				}
				else
				{
					WriteThrowUnexpectedCallException(4);
				}
			}

			private void WriteMockedMethodExpectation(
				MockedMethod method,
				KeyValuePair<ParameterValues, ReturnValues> expectedParameterValues,
				IList<ParameterInfo> methodParameters)
			{
				WriteMockedMethodExpectationCondition(method, expectedParameterValues.Key, methodParameters);
				memberText.AppendLine("\t\t\t\t{");
				memberText.Append("\t\t\t\t\t");
				WriteMockedMethodCallExpectation(method, expectedParameterValues, methodParameters);
				if (method.Method.ReturnType == typeof(void))
				{
					memberText.AppendLine("\t\t\t\t\treturn;");
				}

				memberText.AppendLine("\t\t\t\t}");
				memberText.AppendLine();
			}

			private void WriteMockedMethodExpectationCondition(
				MockedMethod method,
				ParameterValues parameterValues,
				IList<ParameterInfo> methodParameters)
			{
				memberText.Append("\t\t\t\tif (");
				WriteGenericMockedMethodExpectationCondition(method, parameterValues);
				bool isFirst = parameterValues.GenericParameterTypes.Length == 0;
				for (int index = 0; index < parameterValues.Values.Length; index++)
				{
					object parameterValue = parameterValues.Values[index];
					Type parameterType = methodParameters[index].ParameterType;
					if (IsConstant(parameterValue, parameterType) && !parameterType.IsByRef)
					{
						if (isFirst)
						{
							isFirst = false;
						}
						else
						{
							memberText.AppendLine(" &&").AppendFormat("\t\t\t\t\t");
						}

						memberText.AppendFormat(
							"Equals({0}, {1})",
							methodParameters[index].Name,
							GetConstantValue(parameterValue, parameterType));
					}
				}

				memberText.AppendLine(")");
			}

			private void WriteGenericMockedMethodExpectationCondition(MockedMethod method, ParameterValues parameterValues)
			{
				if (parameterValues.GenericParameterTypes.Length == 0)
				{
					return;
				}

				const string equalsFormat = "typeof({0}) == typeof({1})";
				bool isFirst = true;
				foreach (string condition in method.Method.GetGenericArguments()
					.Select((type, index) => string.Format(equalsFormat, type.Name, GetTypeName(parameterValues.GenericParameterTypes[index]))))
				{
					if (isFirst)
					{
						isFirst = false;
					}
					else
					{
						memberText.AppendLine(" &&").AppendFormat("\t\t\t\t\t");
					}

					memberText.Append(condition);
				}
			}

			private void WriteMockedMethodCallExpectation(
				MockedMethod method,
				KeyValuePair<ParameterValues, ReturnValues> expectedParameterValues,
				IList<ParameterInfo> methodParameters)
			{
				bool isVoidReturnType = method.Method.ReturnType == typeof(void);
				string expectationFieldName = GetExpectationFieldName(method.Method);
				string expectationClassName = isVoidReturnType ? "MethodCallExpectation" : "MethodCallReturnExpectation";
				fields.AppendFormat(
					"\t\t\tprivate readonly {0} {1};",
					expectationClassName,
					expectationFieldName).AppendLine();
				constructorText.AppendFormat("\t\t\t\t{0} = new {1}(", expectationFieldName, expectationClassName).AppendLine();
				WriteMethodDescription(method, expectedParameterValues, methodParameters);
				constructorText.AppendFormat("\t\t\t\t\t{0}", expectedParameterValues.Value.Count);
				if (!isVoidReturnType)
				{
					WriteReturnValues(expectedParameterValues.Value, method.Method.ReturnType);
				}

				constructorText.AppendLine(");");
				WriteMockedMethodCallExpectation(expectationFieldName, method.Method.ReturnType);
			}

			private void WriteMockedMethodCallExpectation(string expectationFieldName, Type returnType)
			{
				if (returnType == typeof(void))
				{
					memberText.AppendFormat("{0}.RegisterCall();", expectationFieldName);
				}
				else
				{
					memberText.AppendFormat("return {0}.Return<{1}>();", expectationFieldName, GetTypeName(returnType));
				}

				memberText.AppendLine();
			}

			private string GetExpectationFieldName(MethodBase method)
			{
				return GetExpectationFieldName(method.Name);
			}

			private string GetExpectationFieldName(string expectationFieldName)
			{
				expectationFieldName = string.Concat(
					expectationFieldName.Substring(0, 1).ToLower(),
					expectationFieldName.Substring(1),
					"CallExpectation");
				if (methodCallExpectations.ContainsKey(expectationFieldName))
				{
					methodCallExpectations[expectationFieldName]++;
					expectationFieldName = string.Concat(expectationFieldName, methodCallExpectations[expectationFieldName]);
				}
				else
				{
					methodCallExpectations.Add(expectationFieldName, 1);
				}

				return expectationFieldName;
			}

			private void WriteMethodDescription(
				MockedMethod method,
				KeyValuePair<ParameterValues, ReturnValues> expectedParameterValues,
				IList<ParameterInfo> methodParameters)
			{
				constructorText.AppendFormat("\t\t\t\t\t@\"{0}.{1}", mockedType.Type.Name, method.Method.Name);
				constructorText.Append("(");
				string comma = string.Empty;
				for (int index = 0; index < expectedParameterValues.Key.Values.Length; index++)
				{
					constructorText.Append(comma);
					comma = ", ";
					object parameterValue = expectedParameterValues.Key.Values[index];
					Type parameterType = methodParameters[index].ParameterType;
					if (IsConstant(parameterValue, parameterType))
					{
						constructorText.Append(GetConstantValue(parameterValue, parameterType).Replace("\"", "\"\""));
					}
					else
					{
						constructorText.AppendFormat("Any<{0}>", GetTypeName(parameterType));
					}
				}

				constructorText.AppendLine(")\",");
			}

			private void WriteReturnValues(IList<object> returnValues, Type returnType)
			{
				if (returnValues.Count == 1 || returnValues.Skip(1).All(returnValue => Equals(returnValue, returnValues[0])))
				{
					WriteReturnValue(returnValues[0], returnType);
				}
				else
				{
					foreach (object returnValue in returnValues)
					{
						WriteReturnValue(returnValue, returnType);
					}
				}
			}

			private void WriteReturnValue(object returnValue, Type returnType)
			{
				constructorText.AppendLine(",").Append("\t\t\t\t\t() => ");
				if (IsConstant(returnValue, returnType))
				{
					constructorText.Append(GetConstantValue(returnValue, returnType));
				}
				else
				{
					constructorText.Append("test.").Append(mockedType.GetVariableName(returnValue));
				}
			}
		}

		private sealed class MockedType
		{
			private readonly CodeGenerator codeGenerator;
			private readonly IDictionary<PropertyInfo, ParameterValuesCollection> mockedProperties;
			private readonly IDictionary<MethodInfo, ParameterValuesCollection> mockedMethods;

			public MockedType(Type type, CodeGenerator codeGenerator)
			{
				Type = type;
				this.codeGenerator = codeGenerator;
				IsPassThroughCall = codeGenerator.IsPassThroughCall(type);
				mockedProperties = new Dictionary<PropertyInfo, ParameterValuesCollection>();
				mockedMethods = new Dictionary<MethodInfo, ParameterValuesCollection>(new MethodInfoEqualityComparer());
				foreach (Type interfaceType in (new[] {type}).Concat(type.GetInterfaces()))
				{
					foreach (PropertyInfo property in interfaceType.GetProperties())
					{
						codeGenerator.AddNamespaces(property);
						mockedProperties.Add(property, new ParameterValuesCollection(new[] {property.PropertyType}));
					}

					foreach (MethodInfo method in interfaceType.GetMethods()
						.Where(method => !method.Attributes.HasFlag(MethodAttributes.SpecialName)))
					{
						codeGenerator.AddNamespaces(method);
						mockedMethods.Add(
							method,
							new ParameterValuesCollection(method.GetParameters().Select(parameter => parameter.ParameterType).ToArray()));
					}
				}
			}

			public Type Type { get; private set; }

			public bool IsPassThroughCall { get; private set; }

			public string ParentClassName
			{
				get { return codeGenerator.ClassName; }
			}

			public IEnumerable<MockedProperty> MockedProperties
			{
				get { return mockedProperties.Select(property => new MockedProperty(property.Key, property.Value)); }
			}

			public IEnumerable<MockedMethod> MockedMethods
			{
				get { return mockedMethods.Select(method => new MockedMethod(method.Key, method.Value)); }
			}

			public void AddMemberCall(MemberCall memberCall)
			{
				if (!AddMemberCall(memberCall, mockedProperties) &&
				    !AddMemberCall(memberCall, mockedMethods))
				{
					throw new NotSupportedException();
				}
			}

			public string GetVariableName(object returnValue)
			{
				return codeGenerator.GetVariableName(returnValue);
			}

			private bool AddMemberCall<T>(MemberCall memberCall, IDictionary<T, ParameterValuesCollection> mockedMembers)
				where T : MemberInfo
			{
				var member = memberCall.Member as T;
				if (member == null)
				{
					return false;
				}

				ParameterValuesCollection parameterValues = mockedMembers[member];
				ReturnValues returnValues;
				if (!parameterValues.TryGetValue(new ParameterValues(memberCall), out returnValues))
				{
					returnValues = codeGenerator.BuildReturnValues(memberCall);
					parameterValues.Add(new ParameterValues(memberCall), returnValues);
				}

				codeGenerator.AddReturnValue(memberCall, returnValues);
				return true;
			}
		}

		private sealed class MockedProperty
		{
			public MockedProperty(PropertyInfo property, ParameterValuesCollection expectedParameterValues)
			{
				Property = property;
				ExpectedParameterValues = expectedParameterValues;
			}

			public PropertyInfo Property { get; private set; }

			public ParameterValuesCollection ExpectedParameterValues { get; private set; }
		}

		private sealed class MockedMethod
		{
			public MockedMethod(MethodInfo method, ParameterValuesCollection expectedParameterValues)
			{
				Method = method;
				ExpectedParameterValues = expectedParameterValues;
			}

			public MethodInfo Method { get; private set; }

			public ParameterValuesCollection ExpectedParameterValues { get; private set; }
		}

		private sealed class ParameterValues
		{
			public ParameterValues()
			{
				GenericParameterTypes = new Type[0];
				Values = new object[0];
			}

			public ParameterValues(MemberCall call)
			{
				var method = call.Member as MethodInfo;
				GenericParameterTypes = method != null && method.IsGenericMethod ? method.GetGenericArguments() : new Type[0];
				Values = call.Parameters;
			}

			public Type[] GenericParameterTypes { get; private set; }

			public object[] Values { get; private set; }
		}

		private sealed class ParameterValuesCollection : Dictionary<ParameterValues, ReturnValues>
		{
			public ParameterValuesCollection(Type[] parameterTypes)
				: base(new ParameterValuesEqualityComparer(parameterTypes))
			{
			}
		}

		private sealed class ParameterValuesEqualityComparer : IEqualityComparer<ParameterValues>
		{
			private readonly Type[] parameterTypes;

			public ParameterValuesEqualityComparer(Type[] parameterTypes)
			{
				this.parameterTypes = parameterTypes;
			}

			public bool Equals(ParameterValues x, ParameterValues y)
			{
				if (x.Values.Length != y.Values.Length || !x.GenericParameterTypes.SequenceEqual(y.GenericParameterTypes))
				{
					return false;
				}

				for (int index = 0; index < x.Values.Length; index++)
				{
					if (IsConstant(x.Values[index], parameterTypes[index]) &&
					    !Equals(x.Values[index], y.Values[index]) &&
					    !x.Values[index].GetType().IsByRef)
					{
						return false;
					}
				}

				return true;
			}

			public int GetHashCode(ParameterValues obj)
			{
				return 1;
			}
		}

		private sealed class MethodInfoEqualityComparer : IEqualityComparer<MethodInfo>
		{
			public bool Equals(MethodInfo x, MethodInfo y)
			{
				return GetMethod(x).Equals(GetMethod(y));
			}

			public int GetHashCode(MethodInfo obj)
			{
				return GetMethod(obj).GetHashCode();
			}

			private static MethodInfo GetMethod(MethodInfo obj)
			{
				return obj.IsGenericMethod ? obj.GetGenericMethodDefinition() : obj;
			}
		}

	}
}

using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Ax.TestingCommon.UnitTests;
using Moq;
using NUnit.Framework;

namespace Ax.UnitTests.IntegrationTestRunners
{
	public abstract class IntegrationMockTest
	{
		private readonly InternalMockTest mockTest;

		protected IntegrationMockTest()
		{
			mockTest = new InternalMockTest(this);
		}

		[SetUp]
		public virtual void SetUp()
		{
			mockTest.SetUp();
		}

		[TearDown]
		public virtual void TearDown()
		{
			mockTest.TearDown();
		}

		protected virtual IUnitTestServiceInstanceProvider GetServiceInstanceProvider(MockRepository moqRepository)
		{
			return mockTest.GetDefaultServiceInstanceProvider(moqRepository);
		}

		protected Mock<T> CreateMock<T>()
			where T : class
		{
			return mockTest.CreateFastMock<T>();
		}

		private sealed class InternalMockTest : MockTest
		{
			private readonly IntegrationMockTest mockTest;

			public InternalMockTest(IntegrationMockTest mockTest)
			{
				this.mockTest = mockTest;
			}

			public IUnitTestServiceInstanceProvider GetDefaultServiceInstanceProvider(MockRepository moqRepository)
			{
				return base.GetServiceInstanceProvider(moqRepository);
			}

			public Mock<T> CreateFastMock<T>()
				where T : class
			{
				FieldInfo field = typeof(Mock<T>).GetField("proxyFactory", BindingFlags.NonPublic | BindingFlags.Static);
				if (field == null)
				{
					throw new NotSupportedException();
				}

				field.SetValue(null, ProxyFactory.Instance);
				return CreateMock<T>();
			}

			protected override IUnitTestServiceInstanceProvider GetServiceInstanceProvider(MockRepository moqRepository)
			{
				return mockTest.GetServiceInstanceProvider(moqRepository);
			}
		}

		private abstract class AbstractProxy : RealProxy, IRemotingTypeInfo
		{
			private readonly Type type;

			protected AbstractProxy(Type type)
				: base(typeof(ContextBoundObject))
			{
				this.type = type;
			}

			public string TypeName
			{
				get { return GetType().Name; }
				set { }
			}

			public bool CanCastTo(Type fromType, object o)
			{
				return fromType == type;
			}

			public sealed override IMessage Invoke(IMessage msg)
			{
				var message = msg as IMethodCallMessage;
				if (message == null)
				{
					throw new NotSupportedException();
				}

				if (message.MethodBase.DeclaringType == typeof(object))
				{
					return CreateReturnMessage(
						message,
						message.MethodBase.Name == "GetType" ? type : message.MethodBase.Invoke(this, message.Args));
				}

				return Invoke(message);
			}

			protected static IMethodReturnMessage CreateReturnMessage(IMethodCallMessage message, object returnValue)
			{
				return new ReturnMessage(returnValue, null, 0, message.LogicalCallContext, message);
			}

			protected static IMethodReturnMessage CreateReturnMessage(IMethodCallMessage message, object[] parameters, object returnValue)
			{
				return new ReturnMessage(returnValue, parameters, parameters.Length, message.LogicalCallContext, message);
			}

			protected abstract IMethodReturnMessage Invoke(IMethodCallMessage message);
		}

		private sealed class ProxyFactory : AbstractProxy
		{
			public static readonly object Instance = new ProxyFactory().GetTransparentProxy();

			private ProxyFactory()
				: base(typeof(Mock<>).Assembly.GetType("Moq.Proxy.IProxyFactory"))
			{
			}

			protected override IMethodReturnMessage Invoke(IMethodCallMessage message)
			{
				if (message.MethodBase.Name != "CreateProxy")
				{
					throw new NotSupportedException();
				}

				return CreateReturnMessage(message, new DefaultProxy((Type)message.Args[0], message.Args[1]).GetTransparentProxy());
			}
		}

		private sealed class DefaultProxy : AbstractProxy
		{
			private static readonly Action<object, object> interceptFunc = ((Func<Action<object, object>>)(() =>
			{
				Type type = typeof(Mock<>).Assembly.GetType("Moq.Proxy.ICallInterceptor");
				ParameterExpression interceptorParameter = Expression.Parameter(typeof(object));
				ParameterExpression contextParameter = Expression.Parameter(typeof(object));
				Expression call = Expression.Call(
					Expression.Convert(interceptorParameter, type),
					type.GetMethod("Intercept"),
					new Expression[] {Expression.Convert(contextParameter, typeof(Mock<>).Assembly.GetType("Moq.Proxy.ICallContext"))});
				return Expression.Lambda<Action<object, object>>(call, interceptorParameter, contextParameter).Compile();
			}))();

			private readonly object interceptor;

			public DefaultProxy(Type type, object interceptor)
				: base(type)
			{
				this.interceptor = interceptor;
			}

			protected override IMethodReturnMessage Invoke(IMethodCallMessage message)
			{
				var callContextProxy = new CallContextProxy(message.Args, (MethodInfo)message.MethodBase);
				object proxy = callContextProxy.GetTransparentProxy();
				interceptFunc(interceptor, proxy);
				return CreateReturnMessage(message, callContextProxy.Arguments, callContextProxy.ReturnValue);
			}
		}

		private sealed class CallContextProxy : AbstractProxy
		{
			private static readonly Type callContextType = typeof(Mock<>).Assembly.GetType("Moq.Proxy.ICallContext");
			private readonly object[] arguments;
			private readonly MethodInfo method;

			public CallContextProxy(object[] arguments, MethodInfo method)
				: base(callContextType)
			{
				this.arguments = arguments;
				this.method = method;
			}

			public object[] Arguments
			{
				get { return arguments; }
			}

			public object ReturnValue { get; private set; }

			protected override IMethodReturnMessage Invoke(IMethodCallMessage message)
			{
				return CreateReturnMessage(message, GetReturnValue(message));
			}

			private object GetReturnValue(IMethodMessage message)
			{
				switch (message.MethodBase.Name)
				{
					case "get_Arguments":
						return arguments;
					case "get_Method":
						return method;
					case "get_ReturnValue":
						return ReturnValue;
					case "set_ReturnValue":
						ReturnValue = message.Args[0];
						return null;
					case "SetArgumentValue":
						arguments[(int)message.Args[0]] = message.Args[1];
						return null;
					default:
						throw new NotSupportedException();
				}
			}
		}
	}
}

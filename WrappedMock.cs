using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Ax.TestingCommon.Extensions;
using Moq;
using Moq.Language.Flow;

namespace Ax.WebIntegrationTests.TestHelpers
{
	public sealed class WrappedMock<T>
		where T : class
	{
		private readonly Mock<T> mock;
		private readonly ICollection<MethodBase> mockedMethods;

		internal WrappedMock(MockRepository repository)
		{
			mock = repository.Create<T>();
			mockedMethods = new HashSet<MethodBase>();
		}

		public T GetObject(T objectToWrap)
		{
			return (T)new ObjectProxy(this, objectToWrap).GetTransparentProxy();
		}

		public ISetup<T> Setup(Expression<Action<T>> expression)
		{
			SetupMockedMethod(expression);
			return mock.Setup(expression);
		}

		public ISetup<T, TResult> Setup<TResult>(Expression<Func<T, TResult>> expression)
		{
			SetupMockedMethod(expression);
			return mock.Setup(expression);
		}

		public ISetup<T> SetupIgnoreArguments(Expression<Action<T>> expression)
		{
			SetupMockedMethod(expression);
			return mock.SetupIgnoreArguments(expression);
		}

		public ISetup<T, TResult> SetupIgnoreArguments<TResult>(Expression<Func<T, TResult>> expression)
		{
			SetupMockedMethod(expression);
			return mock.SetupIgnoreArguments(expression);
		}

		private void SetupMockedMethod(LambdaExpression expression)
		{
			var methodCallExpression = expression.Body as MethodCallExpression;
			if (methodCallExpression != null)
				mockedMethods.Add(methodCallExpression.Method);
		}

		private sealed class ObjectProxy : RealProxy, IRemotingTypeInfo
		{
			private readonly T objectToWrap;
         private readonly Mock<T> mock;
			private readonly ICollection<MethodBase> mockedMethods;

			public ObjectProxy(WrappedMock<T> wrappedMock, T objectToWrap)
				: base(typeof(ContextBoundObject))
			{
				this.objectToWrap = objectToWrap;
				mock = wrappedMock.mock;
				mockedMethods = wrappedMock.mockedMethods;
			}
			
			public string TypeName
			{
				get { return GetType().Name; }
				set { }
			}

			public bool CanCastTo(Type fromType, object o)
			{
				return fromType == typeof(T);
			}

			public override IMessage Invoke(IMessage msg)
			{
				var message = msg as IMethodCallMessage;
				if (message == null || message.MethodBase.DeclaringType == null)
				{
					throw new NotSupportedException();
				}

				object[] parameters = message.Args;
				object returnValue = message.MethodBase.Invoke(
					mockedMethods.Contains(message.MethodBase) ? mock.Object : objectToWrap,
					parameters);
				return new ReturnMessage(returnValue, parameters, parameters.Length, message.LogicalCallContext, message);
			}
		}
	}
}

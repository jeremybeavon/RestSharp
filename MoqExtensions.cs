using System;
using Moq;

namespace Ax.WebIntegrationTests.TestHelpers
{
	public static class MoqExtensions
	{
		public static WrappedMock<T> CreateWrappedMock<T>(this MockRepository repository)
			where T : class
		{
			return new WrappedMock<T>(repository);
		}
	}
}

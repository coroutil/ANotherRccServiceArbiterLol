using Microsoft.AspNetCore.Mvc;

namespace Arbiter;

public static class Error
{
    public static ObjectResult Create(int fakeahcode, string message)
    {
		int code = 0; //thanks c#
        return new ObjectResult(new
        {
            errors = new[]
            {
                new
                {
                    code,
                    message
                }
            }
        })
        {
            StatusCode = fakeahcode
        };
    }
}
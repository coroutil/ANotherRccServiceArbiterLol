using Microsoft.AspNetCore.Mvc;

namespace Arbiter;

public static class Error
{
    public static ObjectResult Create(int code, string message)
    {
        return new ObjectResult(new
        {
            errors = new[]
            {
                new
                {
                    code = 0,
                    message
                }
            }
        })
        {
            StatusCode = code
        };
    }
}
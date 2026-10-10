using Microsoft.EntityFrameworkCore;
using SyncWatch.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace SyncWatch.Tests
{
    public static class TestHelper
    {
        public static DatabaseContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<DatabaseContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new DatabaseContext(options);
        }
    }
}

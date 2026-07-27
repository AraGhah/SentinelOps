using Microsoft.EntityFrameworkCore;

namespace SentinelOps.Api.Data;

public class SentinelOpsDbContext(DbContextOptions<SentinelOpsDbContext> options) : DbContext(options)
{
}

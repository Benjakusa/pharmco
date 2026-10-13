using System.CommandLine;
using Npgsql;
using Pharmco.Core.Auth;
using Pharmco.Core.Provisioning;
using Pharmco.Core.Security;
using Pharmco.Core.Tenants;

namespace Pharmco.Seed;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var connectionStringOption = new Option<string>(
            name: "--connection-string",
            description: "PostgreSQL connection string");
        connectionStringOption.IsRequired = true;

        var superAdminOption = new Option<(string Username, string Password)>(
            name: "--super-admin",
            description: "Super admin credentials (username, password)");
        superAdminOption.IsRequired = true;

        var clientOption = new Option<(string Username, string Password, string Name, string Phone)>(
            name: "--client",
            description: "Client (pharmacy) credentials (username, password, name, phone)");
        clientOption.IsRequired = true;

        return await BuildCommandLine().InvokeAsync(args);
    }

    static Command BuildCommandLine()
    {
        var connectionStringOption = new Option<string>(
            name: "--connection-string",
            description: "PostgreSQL connection string");
        connectionStringOption.IsRequired = true;

        var superAdminOption = new Option<(string Username, string Password)>(
            name: "--super-admin",
            description: "Super admin credentials (username, password)");
        superAdminOption.IsRequired = true;

        var clientOption = new Option<(string Username, string Password, string Name, string Phone)>(
            name: "--client",
            description: "Client (pharmacy) credentials (username, password, name, phone)");
        clientOption.IsRequired = true;

        var rootCommand = new Command("seed", "Provision super admin, client, and seed works");
        rootCommand.AddOption(connectionStringOption);
        rootCommand.AddOption(superAdminOption);
        rootCommand.AddOption(clientOption);

        rootCommand.SetAction(async (result) =>
        {
            var connString = result.GetValueForOption<string>("--connection-string");
            var (superAdminUser, superAdminPass) = result.GetValueForOption<(string, string)>("--super-admin");
            var (clientUser, clientPass, clientName, clientPhone) = result.GetValueForOption<(string, string, string, string)>("--client");

            await SeedAsync(connString!, superAdminUser, superAdminPass, clientName, clientPhone, clientUser, clientPass);
        });

        return rootCommand;
    }
}
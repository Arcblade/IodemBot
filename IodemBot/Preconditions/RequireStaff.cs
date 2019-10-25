﻿using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Iodembot.Preconditions
{
    // Inherit from PreconditionAttribute
    public class RequireStaff : PreconditionAttribute
    {
        // Override the CheckPermissions method
        public override Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context, CommandInfo command, IServiceProvider services)
        {
            if (context.User is SocketGuildUser gUser)
            {
                if (gUser.Roles.Any(r => r.Name == "Admin" || r.Name == "Moderators" || r.Name == "Colosso Guard" || r.Guild.Id != 355558866282348574))
                {
                    return Task.FromResult(PreconditionResult.FromSuccess());
                }
                else
                {
                    return Task.FromResult(PreconditionResult.FromError($"You must be stuff to run this command."));
                }
            }
            else
            {
                return Task.FromResult(PreconditionResult.FromError("You must be in /r/GoldenSun server to run this command."));
            }
        }
    }
}
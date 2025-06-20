﻿global using System;
global using System.Collections.Generic;
global using System.ComponentModel.DataAnnotations;
global using System.IdentityModel.Tokens.Jwt;
global using System.Linq;
global using System.Security.Claims;
global using System.Text.RegularExpressions;
global using System.Threading.Tasks;
global using Duende.IdentityModel;
global using Duende.IdentityServer;
global using Duende.IdentityServer.Configuration;
global using Duende.IdentityServer.Events;
global using Duende.IdentityServer.Extensions;
global using Duende.IdentityServer.Models;
global using Duende.IdentityServer.Services;
global using Duende.IdentityServer.Stores;
global using Duende.IdentityServer.Validation;
global using Microsoft.AspNetCore.Authentication;
global using Microsoft.AspNetCore.Authorization;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Hosting;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Identity;
global using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
global using Microsoft.AspNetCore.Mvc;
global using Microsoft.AspNetCore.Mvc.Filters;
global using Microsoft.AspNetCore.Mvc.Rendering;
global using Microsoft.EntityFrameworkCore;
global using Microsoft.EntityFrameworkCore.Infrastructure;
global using Microsoft.EntityFrameworkCore.Metadata;
global using Microsoft.EntityFrameworkCore.Migrations;
global using eShop.Identity.API;
global using eShop.Identity.API.Configuration;
global using eShop.Identity.API.Data;
global using eShop.Identity.API.Models;
global using eShop.Identity.API.Services;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Options;
global using Polly;
global using eShop.ServiceDefaults;

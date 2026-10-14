# Inventory Manager

**Stack:** C# · .NET 8 · WinForms · Active Directory · xUnit

A desktop IT asset and identity management app for a multi-site organization. It tracks laptops, desktops, phones and peripherals against the people who use them, and drives the user-lifecycle workflows (onboarding, transfers, offboarding) directly against Active Directory.

## Features
- Inventory grid with AD Sync, which flags devices whose user or computer is missing, disabled or mismatched in AD
- Onboarding wizard: account naming rules, OU/manager/group pickers, temporary password
- Single *Transfer User* flow (manager, OU, computer, description, office) with an audit trail written to AD Notes
- Offboarding launcher that checks the offboarding script's Authenticode signature against a pinned thumbprint before running it
- Scheduled terminations (reminder prompts only, never unattended), cell-phone CSV import with fuzzy name matching
- Protected-account policy so bulk operations can never touch admin or service accounts
- Copy user permissions (AD groups, add-only), duplicate-device detection, record edit locks for multi-admin use
- Real LDAP bind for credential checks (works around `PrincipalContext.ValidateCredentials` false positives)
- xUnit test suite in `InventoryManager.Core.Tests`, ClickOnce publishing profile
- `legacy-powershell/` holds the original PowerShell/WinForms version this app replaced

## Running it
Open `InventoryManager.sln` in Visual Studio 2022 (.NET 8 SDK), then build and run `InventoryManager.App`. Run the tests with `dotnet test`. The data file location is configured through environment variables (see the user guide in `docs/`).


---
*Built as an internal IT tool and published here as a portfolio sample. All organization-specific details (domain, tenant, server, site and user names, certificate thumbprints, IPs) have been replaced with placeholders such as `contoso.com`.*

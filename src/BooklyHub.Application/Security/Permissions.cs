namespace BooklyHub.Application.Security;

public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string TenantOwner = "TenantOwner";
    public const string TenantAdmin = "TenantAdmin";
    public const string Manager = "Manager";
    public const string Staff = "Staff";
    public const string Receptionist = "Receptionist";
    public const string Accountant = "Accountant";

    public static readonly IReadOnlyList<string> All =
    [
        PlatformAdmin,
        TenantOwner,
        TenantAdmin,
        Manager,
        Staff,
        Receptionist,
        Accountant
    ];
}

public static class Permissions
{
    public static class Appointments
    {
        public const string Read = "appointments.read";
        public const string Create = "appointments.create";
        public const string Update = "appointments.update";
        public const string Cancel = "appointments.cancel";
        public const string Reschedule = "appointments.reschedule";
        public const string CheckIn = "appointments.checkin";
        public const string Complete = "appointments.complete";
    }

    public static class Customers
    {
        public const string Read = "customers.read";
        public const string Create = "customers.create";
        public const string Update = "customers.update";
        public const string Delete = "customers.delete";
    }

    public static class Services
    {
        public const string Read = "services.read";
        public const string Manage = "services.manage";
    }

    public static class Staff
    {
        public const string Read = "staff.read";
        public const string Manage = "staff.manage";
    }

    public static class Schedules
    {
        public const string Read = "schedules.read";
        public const string Manage = "schedules.manage";
    }

    public static class Resources
    {
        public const string Read = "resources.read";
        public const string Manage = "resources.manage";
    }

    public static class Payments
    {
        public const string Read = "payments.read";
        public const string Manage = "payments.manage";
        public const string Refund = "payments.refund";
    }

    public static class Reports
    {
        public const string Read = "reports.read";
    }

    public static class Audit
    {
        public const string Read = "audit.read";
    }

    public static class Tenancy
    {
        public const string Manage = "tenancy.manage";
    }

    public static readonly IReadOnlyList<string> All =
    [
        Appointments.Read, Appointments.Create, Appointments.Update, Appointments.Cancel,
        Appointments.Reschedule, Appointments.CheckIn, Appointments.Complete,
        Customers.Read, Customers.Create, Customers.Update, Customers.Delete,
        Services.Read, Services.Manage,
        Staff.Read, Staff.Manage,
        Schedules.Read, Schedules.Manage,
        Resources.Read, Resources.Manage,
        Payments.Read, Payments.Manage, Payments.Refund,
        Reports.Read,
        Audit.Read,
        Tenancy.Manage
    ];

    public static IReadOnlyList<string> GetDefaultPermissionsForRole(string role)
    {
        return role switch
        {
            Roles.PlatformAdmin => All,
            Roles.TenantOwner => All.Where(p => p != Tenancy.Manage).ToList(),
            Roles.TenantAdmin => All.Where(p => p != Tenancy.Manage).ToList(),
            Roles.Manager =>
            [
                Appointments.Read, Appointments.Create, Appointments.Update, Appointments.Cancel,
                Appointments.Reschedule, Appointments.CheckIn, Appointments.Complete,
                Customers.Read, Customers.Create, Customers.Update,
                Services.Read, Services.Manage,
                Staff.Read, Staff.Manage,
                Schedules.Read, Schedules.Manage,
                Resources.Read, Resources.Manage,
                Payments.Read, Payments.Manage,
                Reports.Read, Audit.Read
            ],
            Roles.Staff =>
            [
                Appointments.Read, Appointments.Create, Appointments.Update, Appointments.CheckIn, Appointments.Complete,
                Customers.Read,
                Services.Read,
                Staff.Read,
                Schedules.Read,
                Resources.Read
            ],
            Roles.Receptionist =>
            [
                Appointments.Read, Appointments.Create, Appointments.Update, Appointments.Cancel,
                Appointments.Reschedule, Appointments.CheckIn,
                Customers.Read, Customers.Create, Customers.Update,
                Services.Read,
                Staff.Read,
                Schedules.Read,
                Resources.Read,
                Payments.Read, Payments.Manage
            ],
            Roles.Accountant =>
            [
                Payments.Read, Payments.Manage, Payments.Refund,
                Reports.Read,
                Appointments.Read
            ],
            _ => []
        };
    }
}

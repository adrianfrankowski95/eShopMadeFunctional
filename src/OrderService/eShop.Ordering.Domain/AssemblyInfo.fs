module AssemblyInfo

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("eShop.Ordering.Adapters.Common")>]
[<assembly: InternalsVisibleTo("eShop.Ordering.Adapters.Postgres")>]
[<assembly: InternalsVisibleTo("eShop.Ordering.Adapters.RabbitMQ")>]
do ()

namespace eShop.Ordering.Domain.Model.ValueObjects

open eShop.ConstrainedTypes

type Street = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module Street =
    let create = String.NonWhiteSpace.create (nameof Street)
    
    let value = String.NonWhiteSpace.value

type City = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module City =
    let create = String.NonWhiteSpace.create (nameof City)
    
    let value = String.NonWhiteSpace.value

type State = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module State =
    let create = String.NonWhiteSpace.create (nameof State)
    
    let value = String.NonWhiteSpace.value

type Country = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module Country =
    let create = String.NonWhiteSpace.create (nameof Country)
    
    let value = String.NonWhiteSpace.value

type ZipCode = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module ZipCode =
    let create = String.NonWhiteSpace.create (nameof ZipCode)
    
    let value = String.NonWhiteSpace.value

type Address =
    { Street: Street
      City: City
      State: State
      Country: Country
      ZipCode: ZipCode }

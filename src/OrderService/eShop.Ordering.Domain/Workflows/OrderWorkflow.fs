namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type OrderWorkflow<'err, 'ioErr, 'retn> = Workflow<Order.State, Order.Event, 'err, 'ioErr, 'retn>

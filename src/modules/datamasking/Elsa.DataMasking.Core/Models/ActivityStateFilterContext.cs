using System.Collections.Generic;
using Elsa.Models;
using Elsa.Services.Models;

namespace Elsa.DataMasking.Core.Models;

public record ActivityStateFilterContext(IDictionary<string, object?> ActivityData, IActivityBlueprint ActivityBlueprint);
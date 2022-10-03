// This file is part of the ArmoniK project
// 
// Copyright (C) ANEO, 2021-2022. All rights reserved.
//   W. Kirschenmann   <wkirschenmann@aneo.fr>
//   J. Gurhem         <jgurhem@aneo.fr>
//   D. Dubuc          <ddubuc@aneo.fr>
//   L. Ziane Khodja   <lzianekhodja@aneo.fr>
//   F. Lemaitre       <flemaitre@aneo.fr>
//   S. Djebbar        <sdjebbar@aneo.fr>
//   J. Fonseca        <jfonseca@aneo.fr>
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using ArmoniK.Api.gRPC.V1.Tasks;

using FluentValidation;

namespace ArmoniK.Core.Common.gRPC.Validators;

/// <summary>
///   Validator for <see cref="ListTasksRequest" />
/// </summary>
public class ListTasksRequestValidator : AbstractValidator<ListTasksRequest>
{
  /// <summary>
  ///   Initializes a validator for <see cref="ListTasksRequest" />
  /// </summary>
  public ListTasksRequestValidator()
  {
    RuleFor(request => request.Page)
      .NotNull()
      .GreaterThanOrEqualTo(0)
      .WithName($"{nameof(ListTasksRequest)}.{nameof(ListTasksRequest.Page)}");
    RuleFor(request => request.PageSize)
      .NotNull()
      .GreaterThanOrEqualTo(0)
      .WithName($"{nameof(ListTasksRequest)}.{nameof(ListTasksRequest.PageSize)}");
    RuleFor(request => request.Filter)
      .NotNull()
      .NotEmpty()
      .WithName($"{nameof(ListTasksRequest)}.{nameof(ListTasksRequest.Filter)}");
    RuleFor(request => request.Sort)
      .NotNull()
      .NotEmpty()
      .WithName($"{nameof(ListTasksRequest)}.{nameof(ListTasksRequest.Sort)}")
      .DependentRules(() =>
                      {
                        RuleFor(request => request.Sort.Direction)
                          .NotNull()
                          .NotEmpty()
                          .WithName($"{nameof(ListTasksRequest)}.{nameof(ListTasksRequest.Sort)}.{nameof(ListTasksRequest.Sort.Direction)}");
                        RuleFor(request => request.Sort.Field)
                          .NotNull()
                          .NotEmpty()
                          .WithName($"{nameof(ListTasksRequest)}.{nameof(ListTasksRequest.Sort)}.{nameof(ListTasksRequest.Sort.Field)}");
                      });
  }
}

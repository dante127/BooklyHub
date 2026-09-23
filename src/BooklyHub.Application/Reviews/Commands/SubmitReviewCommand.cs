using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Reviews;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Reviews.Commands;

public record ReviewDto(
    Guid Id,
    Guid TenantId,
    Guid AppointmentId,
    Guid CustomerId,
    string CustomerName,
    Guid StaffId,
    string StaffName,
    Guid ServiceId,
    string ServiceName,
    int Rating,
    string? Comment,
    DateTime CreatedAtUtc);

public record SubmitReviewCommand(
    Guid TenantId,
    Guid AppointmentId,
    int Rating,
    string? Comment = null) : IRequest<ReviewDto>;

public class SubmitReviewCommandValidator : AbstractValidator<SubmitReviewCommand>
{
    public SubmitReviewCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.AppointmentId).NotEmpty();
        RuleFor(x => x.Rating).InclusiveBetween(1, 5);
        RuleFor(x => x.Comment).MaximumLength(2000);
    }
}

public class SubmitReviewCommandHandler : IRequestHandler<SubmitReviewCommand, ReviewDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public SubmitReviewCommandHandler(IApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ReviewDto> Handle(SubmitReviewCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _db.Appointments
            .Include(a => a.Customer)
            .Include(a => a.Staff)
            .Include(a => a.Service)
            .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

        if (appointment.Status != AppointmentStatus.Completed)
        {
            throw new BusinessRuleValidationException("AppointmentNotCompleted", "Reviews can only be submitted for completed appointments.");
        }

        var existingReview = await _db.Reviews
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == request.TenantId && r.AppointmentId == request.AppointmentId, cancellationToken);

        if (existingReview)
        {
            throw new BusinessRuleValidationException("DuplicateReview", "A review has already been submitted for this appointment.");
        }

        var review = Review.Create(
            request.TenantId,
            appointment.Id,
            appointment.CustomerId,
            appointment.StaffId,
            appointment.ServiceId,
            request.Rating,
            request.Comment,
            _currentUser.UserId?.ToString());

        _db.Reviews.Add(review);
        await _db.SaveChangesAsync(cancellationToken);

        return new ReviewDto(
            review.Id,
            review.TenantId,
            review.AppointmentId,
            review.CustomerId,
            appointment.Customer?.FullName ?? "",
            review.StaffId,
            appointment.Staff?.FullName ?? "",
            review.ServiceId,
            appointment.Service?.Name ?? "",
            review.Rating,
            review.Comment,
            review.CreatedAtUtc);
    }
}

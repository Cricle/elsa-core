using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Elsa.Models;
using Elsa.Persistence.EntityFramework.Core.Extensions;
using Elsa.Persistence.EntityFramework.Core.Services;
using Elsa.Persistence.Specifications;
using Microsoft.EntityFrameworkCore;

namespace Elsa.Persistence.EntityFramework.Core.Stores
{
    public abstract class EntityFrameworkStore<T, TContext> : IStore<T> where T : class, IEntity where TContext:DbContext
    {
        private readonly IMapper _mapper;
        private readonly SemaphoreSlim _semaphore = new(1);

        protected EntityFrameworkStore(IContextFactory<TContext> dbContextFactory, IMapper mapper)
        {
            _mapper = mapper;
            DbContextFactory = dbContextFactory;
        }

        protected IContextFactory<TContext> DbContextFactory { get; }

        public async Task SaveAsync(T entity, CancellationToken cancellationToken)
        {
            await _semaphore.WaitAsync(cancellationToken);

            try
            {
                await DoWork(async dbContext =>
                {
                    var dbSet = dbContext.Set<T>();
                    var existingEntity = await dbSet.FindAsync(new object[] { entity.Id }, cancellationToken);

                    if (existingEntity == null)
                    {
                        await dbSet.AddAsync(entity, cancellationToken);
                        existingEntity = entity;
                    }
                    else
                    {
                        // Can't use the approach on the next line because we explicitly ignore certain properties (in order for them to be stored in the Data shadow property).
                        // dbContext.Entry(existingEntity).CurrentValues.SetValues(entity);

                        // Therefore using AutoMapper to copy properties instead.
                        existingEntity = _mapper.Map(entity, existingEntity);
                    }

                    OnSaving(dbContext, existingEntity);
                }, cancellationToken);
            }
            catch(Exception ex)
            {
                var a = ex.Message;
                throw;
            }
            finally
            {
                _semaphore.Release();
            }
        }


        public async Task AddAsync(T entity, CancellationToken cancellationToken)
        {
            await DoWork(async dbContext =>
            {
                var dbSet = dbContext.Set<T>();
                await dbSet.AddAsync(entity, cancellationToken);
                OnSaving(dbContext, entity);
            }, cancellationToken);
        }

        public async Task AddManyAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
        {
            var list = entities.ToList();

            if (!list.Any())
                return;

            await DoWork(async dbContext =>
            {
                var dbSet = dbContext.Set<T>();
                await dbSet.AddRangeAsync(list, cancellationToken);

                foreach (var entity in list)
                    OnSaving(dbContext, entity);
            }, cancellationToken);
        }

        public async Task UpdateAsync(T entity, CancellationToken cancellationToken = default)
        {
            await DoWork(dbContext =>
            {
                var dbSet = dbContext.Set<T>();
                dbSet.Attach(entity);
                dbContext.Entry(entity).State = EntityState.Modified;
                OnSaving(dbContext, entity);
            }, cancellationToken);
        }

        public virtual async Task DeleteAsync(T entity, CancellationToken cancellationToken = default) => await DoWork(async dbContext => await dbContext.Set<T>().AsQueryable().Where(x => x.Id == entity.Id).BatchDeleteWithWorkAroundAsync(dbContext, cancellationToken), cancellationToken);

        public virtual async Task<int> DeleteManyAsync(ISpecification<T> specification, CancellationToken cancellationToken = default)
        {
            var filter = MapSpecification(specification);
            return await DoWork(async dbContext => await dbContext.Set<T>().Where(filter).Select(x=>x.Id).AsQueryable().BatchDeleteWithWorkAroundAsync(dbContext, cancellationToken), cancellationToken);
        }

        public async Task<IEnumerable<T>> FindManyAsync(ISpecification<T> specification, IOrderBy<T>? orderBy = default, IPaging? paging = default, CancellationToken cancellationToken = default)
        {
            var filter = MapSpecification(specification);

            return await DoQuery(async dbContext =>
            {
                var dbSet = dbContext.Set<T>();
                var queryable = dbSet.Where(filter);

                if (orderBy != null)
                {
                    var orderByExpression = orderBy.OrderByExpression;
                    queryable = orderBy.SortDirection == SortDirection.Ascending ? queryable.OrderBy(orderByExpression) : queryable.OrderByDescending(orderByExpression);
                }

                if (paging != null)
                    queryable = queryable.Skip(paging.Skip).Take(paging.Take);

                return (await queryable.ToListAsync(cancellationToken)).Select(x => ReadShadowProperties(dbContext, x)).ToList();
            }, cancellationToken);
        }

        private T ReadShadowProperties(TContext dbContext, T entity)
        {
            OnLoading(dbContext, entity);
            return entity;
        }

        public async Task<int> CountAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
            await DoQueryOnSet(async dbSet => await dbSet.CountAsync(MapSpecification(specification), cancellationToken), cancellationToken);

        public async Task<T?> FindAsync(ISpecification<T> specification, CancellationToken cancellationToken = default)
        {
            var filter = MapSpecification(specification);
            return await DoQuery(async dbContext =>
            {
                var dbSet = dbContext.Set<T>();
                var entity = await dbSet.FirstOrDefaultAsync(filter, cancellationToken);
                return entity != null ? ReadShadowProperties(dbContext, entity) : default;
            }, cancellationToken);
        }

        protected ValueTask DoWorkOnSet(Func<DbSet<T>, ValueTask> work, CancellationToken cancellationToken) => DoWork(dbContext => work(dbContext.Set<T>()), cancellationToken);
        protected ValueTask<TResult> DoWorkOnSet<TResult>(Func<DbSet<T>, ValueTask<TResult>> work, CancellationToken cancellationToken) => DoWork(dbContext => work(dbContext.Set<T>()), cancellationToken);
        protected ValueTask DoWorkOnSet(Action<DbSet<T>> work, CancellationToken cancellationToken) => DoWork(dbContext => work(dbContext.Set<T>()), cancellationToken);
        protected ValueTask<TResult> DoQueryOnSet<TResult>(Func<DbSet<T>, ValueTask<TResult>> work, CancellationToken cancellationToken) => DoQuery(dbContext => work(dbContext.Set<T>()), cancellationToken);

        protected async ValueTask DoWork(Func<TContext, ValueTask> work, CancellationToken cancellationToken)
        {
            await using var dbContext = DbContextFactory.CreateDbContext();
            await work(dbContext);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        protected async ValueTask<TResult> DoWork<TResult>(Func<TContext, ValueTask<TResult>> work, CancellationToken cancellationToken)
        {
            await using var dbContext = DbContextFactory.CreateDbContext();
            var result = await work(dbContext);
            await dbContext.SaveChangesAsync(cancellationToken);
            return result;
        }

        protected async ValueTask DoWork(Action<TContext> work, CancellationToken cancellationToken)
        {
            await using var dbContext = DbContextFactory.CreateDbContext();
            work(dbContext);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        
        protected async ValueTask<TResult> DoQuery<TResult>(Func<TContext, ValueTask<TResult>> work, CancellationToken cancellationToken)
        {
            await using var dbContext = DbContextFactory.CreateDbContext();
            var result = await work(dbContext);
            return result;
        }

        protected virtual void OnSaving(TContext dbContext, T entity) => OnSaving(entity);

        protected virtual void OnSaving(T entity)
        {
        }

        protected virtual void OnLoading(TContext dbContext, T entity) => OnLoading(entity);

        protected virtual void OnLoading(T entity)
        {
        }

        protected abstract Expression<Func<T, bool>> MapSpecification(ISpecification<T> specification);

        protected Expression<Func<T, bool>> AutoMapSpecification(ISpecification<T> specification) => specification.ToExpression();

        public async Task<IEnumerable<TOut>> FindManyAsync<TOut>(ISpecification<T> specification, Expression<Func<T, TOut>> funcMapping, IOrderBy<T>? orderBy = null, IPaging? paging = null, CancellationToken cancellationToken = default) where TOut : class
        {
            var filter = MapSpecification(specification);

            return await DoQuery(async dbContext =>
            {
                var dbSet = dbContext.Set<T>();
                var queryable = dbSet.Where(filter);

                if (orderBy != null)
                {
                    var orderByExpression = orderBy.OrderByExpression;
                    queryable = orderBy.SortDirection == SortDirection.Ascending ? queryable.OrderBy(orderByExpression) : queryable.OrderByDescending(orderByExpression);
                }

                if (paging != null)
                    queryable = queryable.Skip(paging.Skip).Take(paging.Take);

                var queryableDto = queryable.Select(funcMapping).AsQueryable();
                return await queryableDto.ToListAsync();

            }, cancellationToken);
        }
    }
}
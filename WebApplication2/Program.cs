using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://26.132.135.106:5555");

var app = builder.Build();

var httpClient = new HttpClient();

// Реальные URL-адреса серверов
const string groundControlUrl = "http://26.21.3.228:5555/dispatcher"; // Используем моки
const string boardServiceUrl = "http://26.125.155.211:5555"; // Используем моки
const string unoServiceUrl = "http://26.132.135.106:5555"; // Используем моки

var baggageQueue = new ConcurrentQueue<BaggageOrder>();
var activeOrders = new ConcurrentDictionary<int, BaggageOrder>();

// Фоновый процесс для обработки заказов
_ = Task.Run(async () =>
{
    while (true)
    {
        if (baggageQueue.TryDequeue(out var order))
        {
            if (order.Type == BaggageOrderType.Discharge)
            {
                _ = Task.Run(() => ProcessDischargeOrderAsync(order));
            }
            else if (order.Type == BaggageOrderType.Load)
            {
                _ = Task.Run(() => ProcessLoadOrderAsync(order));
            }
        }
        await Task.Delay(1000); // Проверяем очередь каждую секунду
    }
});

// Эндпоинт для выгрузки багажа
app.MapPost("/baggage-discharge", async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync<BaggageRequest>();
    if (request == null)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid request");
        return;
    }

    var order = new BaggageOrder
    {
        OrderId = request.OrderId,
        FlightId = request.PlaneId,
        Type = BaggageOrderType.Discharge
    };

    baggageQueue.Enqueue(order);
    activeOrders.TryAdd(order.OrderId, order);

    Console.WriteLine($"Order {order.OrderId} (Type: {order.Type}) accepted for flight {order.FlightId}");
    await context.Response.WriteAsync("Order accepted");
});

// Эндпоинт для загрузки багажа
app.MapPost("/baggage-loading", async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync<BaggageRequest>();
    if (request == null)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid request");
        return;
    }

    var order = new BaggageOrder
    {
        OrderId = request.OrderId,
        FlightId = request.PlaneId,
        Type = BaggageOrderType.Load
    };

    baggageQueue.Enqueue(order);
    activeOrders.TryAdd(order.OrderId, order);

    Console.WriteLine($"Order {order.OrderId} (Type: {order.Type}) accepted for flight {order.FlightId}");
    await context.Response.WriteAsync("Order accepted");
});

async Task ProcessDischargeOrderAsync(BaggageOrder order)
{
    Console.WriteLine($"Processing DISCHARGE order {order.OrderId} for flight {order.FlightId}");
    await Task.Delay(5000);

    // Текущая точка (начальная точка - 299, гараж)
    var state = new MovementState { CurrentPoint = 299 };

    // 1. Запрос на выезд из гаража с повторными попытками
    if (!await RequestGarageExitWithRetry("luggage_car"))
    {
        Console.WriteLine($"Failed to exit garage for order {order.OrderId}");
        return;
    }

    // 2. Получение маршрута до самолета
    var routeToPlane = await GetRouteToPlane(state.CurrentPoint, order.FlightId);
    if (routeToPlane == null)
    {
        Console.WriteLine($"Failed to get route to plane for order {order.OrderId}");
        return;
    }

    // 3-5. Движение к самолету
    if (!await MoveAlongRoute(routeToPlane, state, order.FlightId, "plane"))
    {
        Console.WriteLine($"Failed to move to plane for order {order.OrderId}");
        return;
    }

    //// 6. Уведомление борта о выгрузке багажа
    //if (!await NotifyBoardAboutBaggageOUT(order.FlightId, "out"))
    //{
    //    Console.WriteLine($"Failed to notify board about baggage unloading for order {order.OrderId}");
    //    return;
    //}

    // Имитация задержки для выгрузки багажа
    Console.WriteLine("Baggage out of the plane");
    await Task.Delay(5000); // 5 секунд задержки

    // 7. Получение маршрута до грузового терминала
    var routeToLuggage = await GetRouteToLuggage(state.CurrentPoint);
    if (routeToLuggage == null)
    {
        Console.WriteLine($"Failed to get route to luggage terminal for order {order.OrderId}");
        return;
    }

    // 8-9. Движение к грузовому терминалу
    if (!await MoveAlongRoute(routeToLuggage, state, order.FlightId, "luggage"))
    {
        Console.WriteLine($"Failed to move to luggage terminal for order {order.OrderId}");
        return;
    }

    // Имитация задержки для выгрузки багажа
    Console.WriteLine("Baggage out of the car");
    await Task.Delay(5000); // 5 секунд задержки

    //// 10. Отправка отчета в УНО
    //if (!await ReportSuccessToUNO(order.OrderId, "baggage-service"))
    //{
    //    Console.WriteLine($"Failed to report success to UNO for order {order.OrderId}");
    //    return;
    //}

    // 11. Получение маршрута до гаража
    var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
    if (routeToGarage == null)
    {
        Console.WriteLine($"Failed to get route to garage for order {order.OrderId}");
        return;
    }

    // 12-14. Движение в гараж
    if (!await MoveAlongRoute(routeToGarage, state, order.FlightId, "garage"))
    {
        Console.WriteLine($"Failed to return to garage for order {order.OrderId}");
        return;
    }

    // 15. Уведомление диспетчера о возвращении в гараж
    if (!await NotifyGarageFree(state.CurrentPoint))
    {
        Console.WriteLine($"Failed to notify dispatcher about returning to garage for order {order.OrderId}");
        return;
    }

    Console.WriteLine($"DISCHARGE order {order.OrderId} completed successfully");
    activeOrders.TryRemove(order.OrderId, out _);
}

async Task ProcessLoadOrderAsync(BaggageOrder order)
{
    Console.WriteLine($"Processing LOAD order {order.OrderId} for flight {order.FlightId}");
    await Task.Delay(5000);

    // Текущая точка (начальная точка - 299, гараж)
    var state = new MovementState { CurrentPoint = 299 };

    // 1. Запрос на выезд из гаража с повторными попытками
    if (!await RequestGarageExitWithRetry("luggage_car"))
    {
        Console.WriteLine($"Failed to exit garage for order {order.OrderId}");
        return;
    }

    // 2. Получение маршрута до грузового терминала
    var routeToLuggage = await GetRouteToLuggage(state.CurrentPoint);
    if (routeToLuggage == null)
    {
        Console.WriteLine($"Failed to get route to luggage terminal for order {order.OrderId}");
        return;
    }

    // 3-5. Движение к грузовому терминалу
    if (!await MoveAlongRoute(routeToLuggage, state, order.FlightId, "luggage"))
    {
        Console.WriteLine($"Failed to move to luggage terminal for order {order.OrderId}");
        return;
    }

    // Имитация задержки для загрузки багажа
    Console.WriteLine("Baggage loaded into the car");
    await Task.Delay(5000); // 5 секунд задержки

    // 6. Получение маршрута до самолета
    var routeToPlane = await GetRouteToPlane(state.CurrentPoint, order.FlightId);
    if (routeToPlane == null)
    {
        Console.WriteLine($"Failed to get route to plane for order {order.OrderId}");
        return;
    }

    // 7-8. Движение к самолету
    if (!await MoveAlongRoute(routeToPlane, state, order.FlightId, "plane"))
    {
        Console.WriteLine($"Failed to move to plane for order {order.OrderId}");
        return;
    }

    // Имитация задержки для загрузки багажа
    Console.WriteLine("Baggage loaded into the plane");
    await Task.Delay(5000); // 5 секунд задержки

    // 9. Уведомление борта о загрузке багажа
    //if (!await NotifyBoardAboutBaggage(order.FlightId))
    //{
    //    Console.WriteLine($"Failed to notify board about baggage loading for order {order.OrderId}");
    //    return;
    //}

    // 10. Отправка отчета в УНО
    //if (!await ReportSuccessToUNO(order.OrderId, "baggage-service"))
    //{
    //    Console.WriteLine($"Failed to report success to UNO for order {order.OrderId}");
    //    return;
    //}

    // 11. Получение маршрута до гаража
    var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
    if (routeToGarage == null)
    {
        Console.WriteLine($"Failed to get route to garage for order {order.OrderId}");
        return;
    }

    // 12-14. Движение в гараж
    if (!await MoveAlongRoute(routeToGarage, state, order.FlightId, "garage"))
    {
        Console.WriteLine($"Failed to return to garage for order {order.OrderId}");
        return;
    }

    // 15. Уведомление диспетчера о возвращении в гараж
    if (!await NotifyGarageFree(state.CurrentPoint))
    {
        Console.WriteLine($"Failed to notify dispatcher about returning to garage for order {order.OrderId}");
        return;
    }

    Console.WriteLine($"LOAD order {order.OrderId} completed successfully");
    activeOrders.TryRemove(order.OrderId, out _);
}

async Task<bool> RequestGarageExitWithRetry(string vehicleType)
{
    while (true)
    {
        if (await RequestGarageExit(vehicleType))
        {
            Console.WriteLine($"Successfully exited garage for vehicle type {vehicleType}");
            return true;
        }
        Console.WriteLine($"Failed to exit garage. Retrying in 2 seconds...");
        await Task.Delay(2000); // Повторная попытка через 2 секунды
    }
}

async Task<bool> RequestGarageExit(string vehicleType)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/garage/{vehicleType}");
    return response.IsSuccessStatusCode;
}

async Task<List<int>> GetRouteToPlane(int currentPoint, int planeId)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/plane/{currentPoint}/{planeId}");
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadFromJsonAsync<List<int>>();
    }
    Console.WriteLine($"Failed to get route to plane from {currentPoint} for plane {planeId}");
    return null;
}

async Task<List<int>> GetRouteToLuggage(int currentPoint)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/luggage");
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadFromJsonAsync<List<int>>();
    }
    Console.WriteLine($"Failed to get route to luggage terminal from {currentPoint}");
    return null;
}

async Task<List<int>> GetRouteToGarage(int currentPoint)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/garage");
    if (response.IsSuccessStatusCode)
    {
        return await response.Content.ReadFromJsonAsync<List<int>>();
    }
    Console.WriteLine($"Failed to get route to garage from {currentPoint}");
    return null;
}

async Task<bool> MoveAlongRoute(List<int> route, MovementState state, int flightId, string routeType)
{
    int lastPoint = state.CurrentPoint;
    int newRouteAttempts = 0; // Счетчик запросов нового маршрута

    while (true)
    {
        foreach (var targetPoint in route)
        {
            // Запрос разрешения на передвижение
            if (await RequestMovementWithRetry(state.CurrentPoint, targetPoint, state))
            {
                // Обновляем текущую точку
                state.CurrentPoint = targetPoint;
                Console.WriteLine($"Moved to point {state.CurrentPoint}");
                // Имитация времени движения
                await Task.Delay(500);

                // Сбрасываем счетчик при успешном перемещении
                state.AttemptsWithoutMovement = 0;
            }
            else
            {
                Console.WriteLine($"Failed to get permission to move from {state.CurrentPoint} to {targetPoint}");

                // Если не двигаемся, увеличиваем счетчик
                if (state.CurrentPoint == lastPoint)
                {
                    state.AttemptsWithoutMovement++;
                    if (state.AttemptsWithoutMovement >= 5)
                    {
                        Console.WriteLine($"Stuck at point {state.CurrentPoint}. Requesting new route...");
                        var newRoute = await GetNewRoute(state.CurrentPoint, flightId, routeType);
                        if (newRoute == null)
                        {
                            Console.WriteLine($"Failed to get new route. Aborting.");
                            return false;
                        }

                        // Увеличиваем счетчик запросов нового маршрута
                        newRouteAttempts++;
                        if (newRouteAttempts >= 3) // Лимит запросов нового маршрута
                        {
                            Console.WriteLine($"Too many attempts to get new route. Aborting.");
                            return false;
                        }

                        // Продолжаем движение по новому маршруту
                        route = newRoute;
                        break; // Выходим из цикла foreach и начинаем заново с новым маршрутом
                    }
                }
                else
                {
                    // Если текущая точка изменилась, сбрасываем счетчик
                    state.AttemptsWithoutMovement = 0;
                }

                lastPoint = state.CurrentPoint;
            }
        }

        // Если все точки маршрута пройдены, возвращаем true
        if (state.CurrentPoint == route[route.Count - 1])
        {
            return true;
        }
    }
}

async Task<bool> RequestMovementWithRetry(int from, int to, MovementState state)
{
    for (int i = 0; i < 5; i++)
    {
        if (await RequestMovement(from, to))
        {
            return true;
        }
        Console.WriteLine($"Failed to get permission to move from {from} to {to}. Retrying...");
        await Task.Delay(1000); // Повторная попытка через 1 секунду
    }
    return false;
}

async Task<bool> RequestMovement(int from, int to)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/point/{from}/{to}");
    return response.IsSuccessStatusCode;
}

async Task<List<int>> GetNewRoute(int currentPoint, int flightId, string routeType)
{
    Console.WriteLine("Getting new route");
    if (routeType == "plane")
    {
        return await GetRouteToPlane(currentPoint, flightId);
    }
    else if (routeType == "luggage")
    {
        return await GetRouteToLuggage(currentPoint);
    }
    else if (routeType == "garage")
    {
        return await GetRouteToGarage(currentPoint);
    }
    return null;
}

async Task<bool> NotifyBoardAboutBaggageOUT(int aircraftId, string action)
{
    var response = await httpClient.GetAsync($"{boardServiceUrl}/baggage_{action}/{aircraftId}");
    return response.IsSuccessStatusCode;
}

async Task<bool> NotifyBoardAboutBaggage(int aircraftId)
{
    var response = await httpClient.GetAsync($"{boardServiceUrl}/baggage/{aircraftId}");
    return response.IsSuccessStatusCode;
}

async Task<bool> ReportSuccessToUNO(int orderId, string serviceName)
{
    var response = await httpClient.GetAsync($"{unoServiceUrl}/uno/api/v1/order/successReport/{orderId}/{serviceName}");
    return response.IsSuccessStatusCode;
}

async Task<bool> NotifyGarageFree(int endPoint)
{
    var response = await httpClient.DeleteAsync($"{groundControlUrl}/garage/free/{endPoint}");
    return response.IsSuccessStatusCode;
}

app.Run();

// Модели данных
public class BaggageRequest
{
    public int PlaneId { get; set; }
    public int OrderId { get; set; }
}

public class BaggageOrder
{
    public int OrderId { get; set; }
    public int FlightId { get; set; }
    public BaggageOrderType Type { get; set; }
}

public enum BaggageOrderType
{
    Discharge,
    Load
}

// Класс для хранения состояния текущей точки
public class MovementState
{
    public int CurrentPoint { get; set; }
    public int AttemptsWithoutMovement { get; set; } = 0; // Счетчик неудачных попыток 
}
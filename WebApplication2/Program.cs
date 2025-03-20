using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://26.132.135.106:5555");

var app = builder.Build();

var httpClient = new HttpClient();

// Реальные URL-адреса серверов
const string groundControlUrl = "http://26.21.3.228:5555/dispatcher"; // Используем моки
const string boardServiceUrl = "http://26.125.155.211:5555"; // Используем моки
const string unoServiceUrl = "http://26.53.143.176:5555"; // Используем моки
const string table = "http://26.228.200.110:5555"; // Используем моки

var baggageQueue = new ConcurrentQueue<BaggageOrder>();
var activeOrders = new ConcurrentDictionary<int, BaggageOrder>();

// Фоновый процесс для обработки заказов
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            if (baggageQueue.TryDequeue(out var order))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine($"Started processing order {order.OrderId} (Type: {order.Type})");

                        if (order.Type == BaggageOrderType.Discharge)
                        {
                            await ProcessDischargeOrderAsync(order);
                        }
                        else if (order.Type == BaggageOrderType.Load)
                        {
                            await ProcessLoadOrderAsync(order);
                        }

                        Console.WriteLine($"Finished processing order {order.OrderId} (Type: {order.Type})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing order {order.OrderId}: {ex.Message}");
                        baggageQueue.Enqueue(order); // Возвращаем заказ в очередь для повторной обработки
                    }
                });
            }

            await Task.Delay(500); // Уменьшаем задержку для более быстрой реакции на новые заказы
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in background task: {ex.Message}");
        }
    }
});


// Эндпоинт для выгрузки багажа
app.MapPost("/baggage-discharge", async (HttpContext context) =>
{
    try
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
            OrderId = request.orderId,
            FlightId = request.planeId,
            Type = BaggageOrderType.Discharge
        };

        baggageQueue.Enqueue(order);
        activeOrders.TryAdd(order.OrderId, order);

        Console.WriteLine($"Order {order.OrderId} (Type: {order.Type}) accepted for flight {order.FlightId}");
        await context.Response.WriteAsync("Order accepted");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in /baggage-discharge: {ex.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal server error");
    }
});

// Эндпоинт для загрузки багажа
app.MapPost("/baggage-loading", async (HttpContext context) =>
{
    try
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
            OrderId = request.orderId,
            FlightId = request.planeId,
            Type = BaggageOrderType.Load
        };

        baggageQueue.Enqueue(order);
        activeOrders.TryAdd(order.OrderId, order);

        Console.WriteLine($"Order {order.OrderId} (Type: {order.Type}) accepted for flight {order.FlightId}");
        await context.Response.WriteAsync("Order accepted");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in /baggage-loading: {ex.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal server error");
    }
});

async Task ProcessDischargeOrderAsync(BaggageOrder order)
{
    try
    {
        Console.WriteLine($"Processing DISCHARGE order {order.OrderId} for flight {order.FlightId}");
        //await TimeOut(30);

        var state = new MovementState { CurrentPoint = 299 };

        if (!await RequestGarageExitWithRetry("luggage_car"))
        {
            Console.WriteLine($"Failed to exit garage for order {order.OrderId}");
            return;
        }

        var routeToPlane = await GetRouteGarageToPlane(state.CurrentPoint, order.FlightId);
        if (routeToPlane == null)
        {
            Console.WriteLine($"Failed to get route to plane for order {order.OrderId}");
            return;
        }

        if (!await MoveAlongRoute(routeToPlane, state, order.FlightId, "g_to_plane"))
        {
            Console.WriteLine($"Failed to move to plane for order {order.OrderId}");
            return;
        }

        //if (!await NotifyBoardAboutBaggageOUT(order.planeId, "out"))
        //{
        //    Console.WriteLine($"[DISCHARGE] Failed to notify board about baggage unloading for order {order.orderId}");
        //    return;
        //}
        Console.WriteLine("Baggage out of the plane");
        //await TimeOut(50);
        await Task.Delay(1000);


        var routeToLuggage = await GetRoutePlaneToLuggage(state.CurrentPoint);
        if (routeToLuggage == null)
        {
            Console.WriteLine($"Failed to get route to luggage terminal for order {order.OrderId}");
            return;
        }

        if (!await MoveAlongRoute(routeToLuggage, state, order.FlightId, "p_to_luggage"))
        {
            Console.WriteLine($"Failed to move to luggage terminal for order {order.OrderId}");
            return;
        }

        Console.WriteLine("Baggage out of the car");
        await Task.Delay(1000);
        //await TimeOut(50);

        //if (!await ReportSuccessToUNO(order.OrderId, "discharge"))
        //{
        //    Console.WriteLine($"Failed to report success to UNO for order {order.OrderId}");
        //    return;
        //}

        var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
        if (routeToGarage == null)
        {
            Console.WriteLine($"Failed to get route to garage for order {order.OrderId}");
            return;
        }

        if (!await MoveAlongRoute(routeToGarage, state, order.FlightId, "garage"))
        {
            Console.WriteLine($"Failed to return to garage for order {order.OrderId}");
            return;
        }

        if (!await NotifyGarageFree(state.CurrentPoint))
        {
            Console.WriteLine($"Failed to notify dispatcher about returning to garage for order {order.OrderId}");
            return;
        }

        Console.WriteLine($"DISCHARGE order {order.OrderId} completed successfully");
        activeOrders.TryRemove(order.OrderId, out _);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing DISCHARGE order {order.OrderId}: {ex.Message}");
    }
}

async Task ProcessLoadOrderAsync(BaggageOrder order)
{
    try
    {
        Console.WriteLine($"Processing LOAD order {order.OrderId} for flight {order.FlightId}");
        //await TimeOut(30);

        var state = new MovementState { CurrentPoint = 299 };

        if (!await RequestGarageExitWithRetry("luggage_car"))
        {
            Console.WriteLine($"Failed to exit garage for order {order.OrderId}");
            return;
        }

        var routeToLuggage = await GetRouteGarageToLuggage(state.CurrentPoint);
        if (routeToLuggage == null)
        {
            Console.WriteLine($"Failed to get route to luggage terminal for order {order.OrderId}");
            return;
        }

        if (!await MoveAlongRoute(routeToLuggage, state, order.FlightId, "g_to_luggage"))
        {
            Console.WriteLine($"Failed to move to luggage terminal for order {order.OrderId}");
            return;
        }

        //if (!await NotifyBoardAboutBaggage(order.planeId)
        //{
        //    Console.WriteLine($"[LOAD] Failed to notify board about baggae loading for order {order.orderId}");
        //    return;
        //}
        Console.WriteLine("Baggage loaded into the car");
        //await TimeOut(50);
        await Task.Delay(1000);

        var routeToPlane = await GetRouteLuggageToPlane(state.CurrentPoint, order.FlightId);
        if (routeToPlane == null)
        {
            Console.WriteLine($"Failed to get route to plane for order {order.OrderId}");
            return;
        }

        if (!await MoveAlongRoute(routeToPlane, state, order.FlightId, "l_to_plane"))
        {
            Console.WriteLine($"Failed to move to plane for order {order.OrderId}");
            return;
        }

        Console.WriteLine("Baggage loaded into the plane");
        //await TimeOut(50);
        await Task.Delay(1000);

        //if (!await ReportSuccessToUNO(order.OrderId, "loading"))
        //{
        //    Console.WriteLine($"Failed to report success to UNO for order {order.OrderId}");
        //    return;
        //}

        var routeToGarage = await GetRouteToGarage(state.CurrentPoint);
        if (routeToGarage == null)
        {
            Console.WriteLine($"Failed to get route to garage for order {order.OrderId}");
            return;
        }

        if (!await MoveAlongRoute(routeToGarage, state, order.FlightId, "garage"))
        {
            Console.WriteLine($"Failed to return to garage for order {order.OrderId}");
            return;
        }

        if (!await NotifyGarageFree(state.CurrentPoint))
        {
            Console.WriteLine($"Failed to notify dispatcher about returning to garage for order {order.OrderId}");
            return;
        }

        Console.WriteLine($"LOAD order {order.OrderId} completed successfully");
        activeOrders.TryRemove(order.OrderId, out _);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing LOAD order {order.OrderId}: {ex.Message}");
    }
}

async Task<bool> RequestGarageExitWithRetry(string vehicleType)
{
    while (true)
    {
        try
        {
            if (await RequestGarageExit(vehicleType))
            {
                Console.WriteLine($"Successfully exited garage for vehicle type {vehicleType}");
                return true;
            }
            Console.WriteLine($"Failed to exit garage. Retrying in 2 seconds...");
            await Task.Delay(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in RequestGarageExitWithRetry: {ex.Message}");
            await Task.Delay(2000);
        }
    }
}

async Task<bool> RequestGarageExit(string vehicleType)
{
    try
    {
        // Отправляем GET-запрос
        var response = await httpClient.GetAsync($"{groundControlUrl}/garage/{vehicleType}");

        // Проверяем, что ответ успешный
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to request garage exit for {vehicleType}. Status code: {response.StatusCode}");
            return false;
        }

        // Читаем содержимое ответа как строку
        var content = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Response content: {content}");

        // Десериализуем JSON в объект
        try
        {
            var result = JsonSerializer.Deserialize<bool>(content);
            return result;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Failed to deserialize response: {ex.Message}");
            return false;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in RequestGarageExit: {ex.Message}");
        return false;
    }
}

async Task<List<int>> GetRouteGarageToPlane(int currentPoint, int planeId)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/plane/{currentPoint}/{planeId}");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to plane from {currentPoint} for plane {planeId}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToPlane: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRoutePlaneToLuggage(int currentPoint)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/plane/{currentPoint}/luggage");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to luggage terminal from {currentPoint}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToLuggage: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRouteToGarage(int currentPoint)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/garage");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to garage from {currentPoint}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToGarage: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRouteGarageToLuggage(int currentPoint)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/luggage");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to garage from {currentPoint}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToGarage: {ex.Message}");
        return null;
    }
}

async Task<List<int>> GetRouteLuggageToPlane(int currentPoint, int planeId)
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/plane/luggage/{currentPoint}/{planeId}");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<int>>();
        }
        Console.WriteLine($"Failed to get route to garage from {currentPoint}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetRouteToGarage: {ex.Message}");
        return null;
    }
}

async Task<bool> MoveAlongRoute(List<int> route, MovementState state, int flightId, string routeType)
{
    state.CurrentRoute = route; // Сохраняем текущий маршрут в состоянии

    while (state.CurrentRoute.Count > 0)
    {
        int targetPoint = state.CurrentRoute[0]; // Берем первую точку маршрута

        // Запрос разрешения на передвижение
        if (await RequestMovement(state.CurrentPoint, targetPoint))
        {
            // Обновляем текущую точку
            state.CurrentPoint = targetPoint;
            Console.WriteLine($"Moved to point {state.CurrentPoint}");

            // Удаляем пройденную точку из маршрута
            state.CurrentRoute.RemoveAt(0);

            // Имитация времени движения
            //await TimeOut(40);
            await Task.Delay(500);

            // Сбрасываем счетчик при успешном перемещении
            state.AttemptsWithoutMovement = 0;
        }
        else
        {
            Console.WriteLine($"Failed to move from {state.CurrentPoint} to {targetPoint}.");
            state.AttemptsWithoutMovement++;

            // Если попыток больше 5, запрашиваем новый маршрут
            if (state.AttemptsWithoutMovement >= 5)
            {
                Console.WriteLine($"Car stuck in traffic. Rebuilding route.");
                var newRoute = await GetNewRoute(state.CurrentPoint, flightId, routeType);
                if (newRoute == null)
                {
                    Console.WriteLine($"Failed to get a new route. Aborting movement.");
                    return false;
                }

                // Обновляем маршрут
                state.CurrentRoute = newRoute;
                state.AttemptsWithoutMovement = 0;
            }

            // Задержка перед повторной попыткой
            await Task.Delay(200);
        }

        // Проверяем, достигли ли конечной точки маршрута
        if (state.CurrentRoute.Count == 0 || state.CurrentPoint == state.CurrentRoute[state.CurrentRoute.Count - 1])
        {
            return true; // Маршрут завершен
        }
    }

    return true; // Маршрут завершен
}

async Task<bool> RequestMovement(int from, int to)
{
    try
    {
        // Отправляем GET-запрос
        var response = await httpClient.GetAsync($"{groundControlUrl}/point/{from}/{to}");

        // Проверяем, что ответ успешный
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to get permission to move from {from} to {to}. Status code: {response.StatusCode}");
            return false;
        }

        // Читаем содержимое ответа как строку
        var content = await response.Content.ReadAsStringAsync();

        // Десериализуем JSON в объект
        var result = JsonSerializer.Deserialize<bool>(content);

        // Возвращаем значение
        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in RequestMovement: {ex.Message}");
        return false; // Возвращает false в случае ошибки
    }
}

async Task<List<int>> GetNewRoute(int currentPoint, int flightId, string routeType)
{
    try
    {
        Console.WriteLine("Getting new route");
        if (routeType == "g_to_plane")
        {
            return await GetRouteGarageToPlane(currentPoint, flightId);
        }
        else if (routeType == "p_to_luggage")
        {
            return await GetRoutePlaneToLuggage(currentPoint);
        }
        else if (routeType == "g_to_luggage")
        {
            return await GetRouteGarageToLuggage(currentPoint);
        }
        else if (routeType == "l_to_plane")
        {
            return await GetRouteLuggageToPlane(currentPoint, flightId);
        }
        else if (routeType == "garage")
        {
            return await GetRouteToGarage(currentPoint);
        }
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in GetNewRoute: {ex.Message}");
        return null;
    }
}

async Task<bool> NotifyBoardAboutBaggageOUT(int aircraftId, string action)
{
    try
    {
        var response = await httpClient.GetAsync($"{boardServiceUrl}/baggage_{action}/{aircraftId}");
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in NotifyBoardAboutBaggageOUT: {ex.Message}");
        return false;
    }
}

async Task<bool> NotifyBoardAboutBaggage(int aircraftId)
{
    try
    {
        var response = await httpClient.GetAsync($"{boardServiceUrl}/baggage/{aircraftId}");
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in NotifyBoardAboutBaggage: {ex.Message}");
        return false;
    }
}

async Task<bool> ReportSuccessToUNO(int orderId, string serviceName)
{
    try
    {
        var url = $"{unoServiceUrl}/uno/api/v1/order/successReport/{orderId}/baggage-{serviceName}";
        var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(url, content);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in ReportSuccessToUNO: {ex.Message}");
        return false;
    }
}

async Task<bool> NotifyGarageFree(int endPoint)
{
    try
    {
        var response = await httpClient.DeleteAsync($"{groundControlUrl}/garage/free/{endPoint}");
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in NotifyGarageFree: {ex.Message}");
        return false;
    }
}

async Task<bool> TimeOut(int time)
{
    try
    {
        var response = await httpClient.GetAsync($"{table}/dep-board/api/v1/time/timeout?timeout={time}");
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in TimeOut: {ex.Message}");
        return false;
    }
}

app.Run();

// Модели данных
public class BaggageRequest
{
    public int planeId { get; set; }
    public int orderId { get; set; }
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

public class MovementState
{
    public int CurrentPoint { get; set; }
    public int AttemptsWithoutMovement { get; set; } = 0;
    public List<int> CurrentRoute { get; set; } // Текущий маршрут
}